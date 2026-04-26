using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DistributedDebugger.Web.V3;

/// <summary>
/// SQLite-backed memory using the sqlite-vec extension. Stores past
/// investigations indexed by an embedding of the bug description and
/// retrieves the most similar past entries via vec0's KNN query.
///
/// Schema notes:
///
///   - Two tables: a regular <c>memory</c> table for payload, and a vec0
///     virtual table <c>vec_memory</c> for the searchable embedding. They're
///     joined on <c>rowid</c>. We could've used vec0 metadata columns to keep
///     everything in one virtual table, but separating payload makes it
///     easier to inspect with a normal sqlite browser and easier to swap
///     vec0 for a different vector index later.
///
///   - Embeddings are stored as JSON arrays in vec0's expected format. vec0
///     also accepts compact binary; JSON is easier to debug.
///
///   - We re-normalise embeddings to unit length on insert. OpenAI
///     embeddings are already unit-length per their docs, but normalising
///     defensively means the distance→similarity conversion below holds even
///     if upstream behaviour changes.
///
/// Distance → similarity:
///
///   sqlite-vec returns squared L2 distance. For unit-length vectors that
///   equals 2 − 2·cos(θ), so cos(θ) = 1 − distance/2. We convert before
///   returning so the rest of the codebase reasons in cosine similarity
///   (familiar territory: 1.0 = identical, 0.0 = orthogonal).
///
/// Connection lifetime:
///
///   One connection per call. Microsoft.Data.Sqlite is single-threaded per
///   connection; opening fresh per call is the simplest correct pattern. The
///   connection is cheap (it's a file open) and the schema-creation runs
///   IF NOT EXISTS so initialisation is idempotent. If we ever hit a
///   throughput problem, switch to a connection pool.
///
/// Native binary requirement:
///
///   This class calls <c>connection.LoadExtension("vec0")</c>. The vec0
///   shared library must be present on the dynamic-loader path of the
///   running process. See <c>scripts/bootstrap-vec.sh</c> in the repo for
///   how to obtain it for your OS — on first build run that script and the
///   binary lands in <c>~/.dd/vec0[.dll/.so/.dylib]</c>. The
///   <see cref="ResolveExtensionPath"/> helper looks there first.
/// </summary>
public sealed class SqliteVecInvestigationMemory : IInvestigationMemory
{
    private const int EmbeddingDimensions = 1536;   // text-embedding-3-small
    private readonly string _connectionString;

    public SqliteVecInvestigationMemory(string dbPath)
    {
        // Make sure the parent directory exists — first run on a fresh box
        // will hit ~/.dd which won't exist yet.
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        InitSchema();
    }

    private void InitSchema()
    {
        using var conn = OpenWithVec();

        // Payload table — vanilla SQLite, no vec0 dependency. Keeps the
        // human-readable bits separate from the searchable embedding.
        ExecuteNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS memory (
              id           TEXT PRIMARY KEY,
              created_at   TEXT NOT NULL,
              description  TEXT NOT NULL,
              hypothesis   TEXT NOT NULL,
              evidence     TEXT NOT NULL,
              ticket_id    TEXT,
              schemas_json TEXT NOT NULL
            );
            """);

        // Vector index — vec0 virtual table. Joined to memory.rowid.
        // The float[1536] declaration is enforced; inserting a
        // wrong-dimension vector throws.
        ExecuteNonQuery(conn, $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS vec_memory USING vec0(
              embedding float[{EmbeddingDimensions}]
            );
            """);
    }

    public async Task WriteAsync(InvestigationMemoryEntry entry, CancellationToken ct)
    {
        if (entry.Embedding.Length != EmbeddingDimensions)
            throw new ArgumentException(
                $"Embedding must be {EmbeddingDimensions}-dim (got {entry.Embedding.Length}).",
                nameof(entry));

        var normalized = Normalise(entry.Embedding.Span);

        await using var conn = OpenWithVec();
        await using var tx = conn.BeginTransaction();

        // Insert payload first — we need the rowid it generates as the link
        // into vec_memory. Using INSERT OR REPLACE keyed by id makes the
        // operation idempotent if the same Id is somehow re-submitted.
        long rowid;
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO memory(id, created_at, description, hypothesis, evidence, ticket_id, schemas_json)
                VALUES (@id, @created, @desc, @hyp, @ev, @ticket, @schemas)
                ON CONFLICT(id) DO UPDATE SET
                  created_at  = excluded.created_at,
                  description = excluded.description,
                  hypothesis  = excluded.hypothesis,
                  evidence    = excluded.evidence,
                  ticket_id   = excluded.ticket_id,
                  schemas_json= excluded.schemas_json
                RETURNING rowid;
                """;
            cmd.Parameters.AddWithValue("@id", entry.Id);
            cmd.Parameters.AddWithValue("@created", entry.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@desc", entry.Description);
            cmd.Parameters.AddWithValue("@hyp", entry.Hypothesis);
            cmd.Parameters.AddWithValue("@ev", entry.EvidenceSummary);
            cmd.Parameters.AddWithValue("@ticket", (object?)entry.TicketId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@schemas", JsonSerializer.Serialize(entry.SchemasIncluded));

            rowid = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        // If we updated an existing row, we need to delete the old vector
        // before inserting the new one — vec0 doesn't UPSERT.
        {
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM vec_memory WHERE rowid = @r;";
            del.Parameters.AddWithValue("@r", rowid);
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Vec0 accepts the embedding as a JSON array literal. We use the
        // 'F' format with InvariantCulture so a comma-locale machine doesn't
        // produce '1,234' for thousands separators (vec0 would reject).
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO vec_memory(rowid, embedding) VALUES (@r, @emb);";
            ins.Parameters.AddWithValue("@r", rowid);
            ins.Parameters.AddWithValue("@emb", FloatsToJsonArray(normalized));
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InvestigationMemoryHit>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        float minSimilarity,
        CancellationToken ct)
    {
        if (queryEmbedding.Length != EmbeddingDimensions)
            throw new ArgumentException(
                $"Query embedding must be {EmbeddingDimensions}-dim.",
                nameof(queryEmbedding));

        var normalized = Normalise(queryEmbedding.Span);

        await using var conn = OpenWithVec();
        using var cmd = conn.CreateCommand();

        // Pull a few extra candidates from vec0 then filter by similarity in
        // C#. We could push the threshold into SQL via WHERE distance < (...)
        // but the algebra (similarity = 1 - distance/2) is clearer in code,
        // and topK is small enough the extra rows don't matter.
        cmd.CommandText = """
            SELECT m.id, m.created_at, m.description, m.hypothesis, m.evidence,
                   m.ticket_id, m.schemas_json, v.distance
            FROM vec_memory v
            JOIN memory m ON m.rowid = v.rowid
            WHERE v.embedding MATCH @q
            ORDER BY v.distance
            LIMIT @k;
            """;
        cmd.Parameters.AddWithValue("@q", FloatsToJsonArray(normalized));
        cmd.Parameters.AddWithValue("@k", topK);

        var hits = new List<InvestigationMemoryHit>(topK);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var distance = (float)reader.GetDouble(7);
            // sqlite-vec returns squared L2 distance. For unit vectors that's
            // 2 - 2·cos(θ), so cos = 1 - dist/2. Clamp to [-1, 1] for safety
            // against tiny FP drift.
            var similarity = Math.Clamp(1f - distance / 2f, -1f, 1f);
            if (similarity < minSimilarity) continue;

            var schemas = JsonSerializer.Deserialize<string[]>(reader.GetString(6))
                          ?? Array.Empty<string>();

            // We don't need the embedding back from a search — caller wants
            // payload only. Use ReadOnlyMemory<float>.Empty to avoid a wasted
            // round-trip through vec0.
            var entry = new InvestigationMemoryEntry(
                Id: reader.GetString(0),
                CreatedAt: DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                Description: reader.GetString(2),
                Hypothesis: reader.GetString(3),
                EvidenceSummary: reader.GetString(4),
                TicketId: reader.IsDBNull(5) ? null : reader.GetString(5),
                SchemasIncluded: schemas,
                Embedding: ReadOnlyMemory<float>.Empty);

            hits.Add(new InvestigationMemoryHit(entry, similarity));
        }
        return hits;
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var conn = OpenWithVec();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memory;";
        var n = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(n);
    }

    // ---- helpers ----

    private SqliteConnection OpenWithVec()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // Enable extension loading and pull in vec0. Microsoft.Data.Sqlite's
        // LoadExtension uses the platform's dynamic-loader search path, so
        // we set up the path candidates first via SetDllDirectory-equivalent
        // logic (see ResolveExtensionPath).
        var extPath = ResolveExtensionPath();
        conn.EnableExtensions(true);
        conn.LoadExtension(extPath);
        conn.EnableExtensions(false);
        return conn;
    }

    /// <summary>
    /// Find the vec0 native binary. Preference order:
    ///
    ///   1. <c>DD_VEC0_PATH</c> env var — escape hatch for unusual setups.
    ///   2. ~/.dd/vec0.{dll,so,dylib} — where bootstrap-vec.sh puts it.
    ///   3. Bare 'vec0' — let the dynamic loader find it on PATH /
    ///      LD_LIBRARY_PATH / DYLD_LIBRARY_PATH.
    ///
    /// Returning a bare name (option 3) lets users who have vec0 installed
    /// system-wide skip the bootstrap dance.
    /// </summary>
    private static string ResolveExtensionPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("DD_VEC0_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ext = OperatingSystem.IsWindows() ? ".dll"
                : OperatingSystem.IsMacOS()   ? ".dylib"
                                              : ".so";
        var bootstrapped = Path.Combine(home, ".dd", "vec0" + ext);
        if (File.Exists(bootstrapped)) return bootstrapped;

        return "vec0";
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Re-normalise a vector to unit length. OpenAI embeddings should already
    /// be unit-length, but doing it again removes the assumption — and
    /// floating-point error from in-flight serialization is negligible.
    /// Returns a fresh array to avoid aliasing the caller's buffer.
    /// </summary>
    private static float[] Normalise(ReadOnlySpan<float> v)
    {
        double sumSq = 0;
        for (int i = 0; i < v.Length; i++) sumSq += v[i] * v[i];
        var norm = Math.Sqrt(sumSq);
        if (norm == 0)
            // All zeros — pathological but possible if upstream goofs. Return
            // as-is; vec0 will store it and just never match anything.
            return v.ToArray();

        var inv = (float)(1.0 / norm);
        var output = new float[v.Length];
        for (int i = 0; i < v.Length; i++) output[i] = v[i] * inv;
        return output;
    }

    /// <summary>
    /// Format a float array as the JSON literal vec0 expects:
    /// <c>[0.123, -0.456, ...]</c>. InvariantCulture keeps comma-locale
    /// machines from breaking the format ('1,234' for 1.234).
    /// </summary>
    private static string FloatsToJsonArray(ReadOnlySpan<float> v)
    {
        // Pre-size a StringBuilder generously: each float is at most ~15 chars.
        var sb = new System.Text.StringBuilder(v.Length * 16);
        sb.Append('[');
        for (int i = 0; i < v.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(v[i].ToString("R", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }
}
