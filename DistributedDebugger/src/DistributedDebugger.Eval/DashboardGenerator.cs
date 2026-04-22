using System.Globalization;
using System.Text;
using System.Text.Json;
using DistributedDebugger.Eval.Internal;

namespace DistributedDebugger.Eval;

/// <summary>
/// Generates a single self-contained HTML dashboard from the CSV files produced
/// by <see cref="RegressionRunner"/>. Static on purpose: regenerated on every
/// eval run, no server required, opens fine from the filesystem.
///
/// The dashboard has three sections:
///
///   1. Latest run summary         — pass/fail per (config, case)
///   2. Run-over-run diff          — which cases flipped since the previous run?
///   3. Trend charts               — pass rate per config over time
///
/// Everything is embedded: data serialised as JSON in a script tag, Chart.js
/// loaded from the Cloudflare CDN. No build step, no bundler, no dependency
/// except a browser.
/// </summary>
public sealed class DashboardGenerator
{
    public async Task<string> GenerateAsync(string resultsDir, CancellationToken ct)
    {
        if (!Directory.Exists(resultsDir))
        {
            throw new DirectoryNotFoundException(
                $"Eval results folder not found: {resultsDir}. Run `debugger eval` first.");
        }

        // Discover and parse every CSV in the folder. Ordered chronologically
        // by filename (our filenames carry a timestamp, so ordinal sort = time
        // order). Corrupt or partial files are skipped with a warning rather
        // than aborting the whole dashboard — a dashboard should be resilient
        // to a single bad run.
        var csvFiles = Directory.GetFiles(resultsDir, "eval-*.csv")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var runs = new List<RunData>();
        foreach (var file in csvFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                runs.Add(await ParseCsvAsync(file, ct));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ⚠ skipping {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (runs.Count == 0)
        {
            throw new InvalidOperationException(
                $"No parseable CSV files in {resultsDir}. Run `debugger eval` first.");
        }

        var html = BuildHtml(runs);
        var outputPath = Path.Combine(resultsDir, "dashboard.html");
        await File.WriteAllTextAsync(outputPath, html, ct);
        return outputPath;
    }

    private static async Task<RunData> ParseCsvAsync(string path, CancellationToken ct)
    {
        // The CSV writer uses a stable, documented header order, so we parse
        // by column index rather than header name. If the header ever changes,
        // this throws and the dashboard skips the file — better to lose one
        // run than to silently show wrong data.
        //
        // Column order (see EvalCommand.WriteCsvAsync):
        //   configName, caseId, passed, causeCorrect, serviceCoverage,
        //   iterations, inputTokens, outputTokens, judgeTokens,
        //   durationSeconds, notes
        var lines = await File.ReadAllLinesAsync(path, ct);
        if (lines.Length < 2)
        {
            throw new InvalidDataException("fewer than 2 lines (expected header + rows)");
        }

        var rows = new List<RowData>(lines.Length - 1);
        for (int i = 1; i < lines.Length; i++)
        {
            var fields = CsvLineSplitter.Split(lines[i]);
            if (fields.Count < 11) continue; // defensive skip
            rows.Add(new RowData(
                ConfigName:    fields[0],
                CaseId:        fields[1],
                Passed:        bool.TryParse(fields[2], out var p) && p,
                CauseCorrect:  bool.TryParse(fields[3], out var c) && c,
                ServiceCoverage: double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) ? sc : 0,
                Iterations:    int.TryParse(fields[5], out var it) ? it : 0,
                InputTokens:   int.TryParse(fields[6], out var inT) ? inT : 0,
                OutputTokens:  int.TryParse(fields[7], out var outT) ? outT : 0,
                JudgeTokens:   int.TryParse(fields[8], out var jt) ? jt : 0,
                DurationSeconds: double.TryParse(fields[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0,
                Notes:         fields[10]
            ));
        }

        // Parse timestamp from filename: eval-YYYYMMDD-HHMMSS.csv
        var name = Path.GetFileNameWithoutExtension(path);
        var stampPart = name.StartsWith("eval-") ? name.Substring(5) : name;
        var timestamp = DateTime.TryParseExact(stampPart, "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts)
            ? ts : File.GetLastWriteTimeUtc(path);

        return new RunData(
            FileName: Path.GetFileName(path),
            Timestamp: timestamp,
            Rows: rows);
    }

    private static string BuildHtml(IReadOnlyList<RunData> runs)
    {
        // Serialise the runs as JSON so the client-side JS can drive everything.
        // Using camelCase for a more JS-friendly shape inside the script tag.
        var json = JsonSerializer.Serialize(runs, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        });

        var latest = runs[^1];
        var generated = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        // Using a raw string literal keeps the HTML legible. Placeholders are
        // marked with {{double braces}} and replaced at the end — otherwise
        // curly braces in the CSS and JS would fight string interpolation.
        var template = """
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>DistributedDebugger — Eval Dashboard</title>
              <script src="https://cdnjs.cloudflare.com/ajax/libs/Chart.js/4.4.1/chart.umd.min.js"></script>
              <style>
                :root {
                  --fg: #1a1a1a;
                  --muted: #666;
                  --bg: #fafafa;
                  --card-bg: #ffffff;
                  --border: #e5e5e5;
                  --pass: #2e7d32;
                  --fail: #c62828;
                  --flip-up: #1565c0;
                  --flip-down: #ef6c00;
                }
                body {
                  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
                  background: var(--bg);
                  color: var(--fg);
                  max-width: 1200px;
                  margin: 0 auto;
                  padding: 24px;
                  line-height: 1.5;
                }
                h1 { font-size: 22px; margin: 0 0 4px; }
                h2 { font-size: 16px; margin: 24px 0 12px; }
                .subtitle { color: var(--muted); font-size: 13px; margin-bottom: 24px; }
                .card {
                  background: var(--card-bg);
                  border: 1px solid var(--border);
                  border-radius: 8px;
                  padding: 16px;
                  margin-bottom: 16px;
                }
                .summary {
                  display: grid;
                  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
                  gap: 12px;
                  margin-bottom: 16px;
                }
                .metric { padding: 12px 16px; }
                .metric .label { color: var(--muted); font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; }
                .metric .value { font-size: 24px; font-weight: 600; margin-top: 4px; }
                table { width: 100%; border-collapse: collapse; font-size: 13px; }
                th, td { padding: 8px 10px; text-align: left; border-bottom: 1px solid var(--border); }
                th { font-weight: 600; color: var(--muted); font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; }
                td.num { text-align: right; font-variant-numeric: tabular-nums; }
                .pass { color: var(--pass); font-weight: 600; }
                .fail { color: var(--fail); font-weight: 600; }
                .flip-pass { background: #e8f5e9; }
                .flip-fail { background: #ffebee; }
                canvas { max-height: 320px; }
                .empty { color: var(--muted); font-style: italic; }
                .notes { color: var(--muted); font-size: 12px; max-width: 400px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .notes:hover { white-space: normal; }
              </style>
            </head>
            <body>
              <h1>DistributedDebugger — Eval Dashboard</h1>
              <div class="subtitle">Generated {{GENERATED}} · {{RUN_COUNT}} run(s) · latest: {{LATEST_FILE}}</div>

              <h2>Latest run summary</h2>
              <div class="card summary" id="summary-tiles"></div>

              <h2>Changes since previous run</h2>
              <div class="card" id="diff-section"></div>

              <h2>Pass rate over time</h2>
              <div class="card"><canvas id="passRateChart"></canvas></div>

              <h2>Token usage over time</h2>
              <div class="card"><canvas id="tokenChart"></canvas></div>

              <h2>Latest run — per-case detail</h2>
              <div class="card"><table id="detail-table"><thead><tr>
                <th>Config</th><th>Case</th><th>Pass</th><th>Cause</th>
                <th>Svc</th><th class="num">Iter</th>
                <th class="num">Tokens (in/out/judge)</th>
                <th class="num">Seconds</th><th>Notes</th>
              </tr></thead><tbody></tbody></table></div>

              <script id="data" type="application/json">{{DATA_JSON}}</script>
              <script>
                const runs = JSON.parse(document.getElementById('data').textContent);
                const latest = runs[runs.length - 1];
                const previous = runs.length > 1 ? runs[runs.length - 2] : null;

                // ---------- helpers ----------
                function passRate(rows) {
                  if (rows.length === 0) return 0;
                  return rows.filter(r => r.passed).length / rows.length;
                }
                function groupBy(arr, keyFn) {
                  const m = new Map();
                  for (const x of arr) {
                    const k = keyFn(x);
                    if (!m.has(k)) m.set(k, []);
                    m.get(k).push(x);
                  }
                  return m;
                }
                const fmtPct = n => (n * 100).toFixed(0) + '%';
                const fmtInt = n => n.toLocaleString();

                // ---------- summary tiles ----------
                const totalPass = latest.rows.filter(r => r.passed).length;
                const totalRuns = latest.rows.length;
                const totalIn = latest.rows.reduce((s,r) => s + r.inputTokens, 0);
                const totalOut = latest.rows.reduce((s,r) => s + r.outputTokens, 0);
                const totalJudge = latest.rows.reduce((s,r) => s + r.judgeTokens, 0);
                const avgIter = totalRuns === 0 ? 0 : latest.rows.reduce((s,r) => s + r.iterations, 0) / totalRuns;

                document.getElementById('summary-tiles').innerHTML = `
                  <div class="metric"><div class="label">Pass rate</div>
                    <div class="value ${totalPass === totalRuns ? 'pass' : 'fail'}">${totalPass}/${totalRuns} (${fmtPct(passRate(latest.rows))})</div></div>
                  <div class="metric"><div class="label">Avg iterations</div>
                    <div class="value">${avgIter.toFixed(1)}</div></div>
                  <div class="metric"><div class="label">Agent tokens (in/out)</div>
                    <div class="value" style="font-size:16px">${fmtInt(totalIn)} / ${fmtInt(totalOut)}</div></div>
                  <div class="metric"><div class="label">Judge tokens</div>
                    <div class="value" style="font-size:16px">${fmtInt(totalJudge)}</div></div>
                `;

                // ---------- diff vs previous ----------
                const diffEl = document.getElementById('diff-section');
                if (!previous) {
                  diffEl.innerHTML = '<div class="empty">No previous run to compare against.</div>';
                } else {
                  // Index previous by (config, case) so we can find flips.
                  const prevMap = new Map();
                  for (const r of previous.rows) {
                    prevMap.set(r.configName + '|' + r.caseId, r);
                  }
                  const flips = [];
                  for (const r of latest.rows) {
                    const prev = prevMap.get(r.configName + '|' + r.caseId);
                    if (prev && prev.passed !== r.passed) {
                      flips.push({ ...r, previousPassed: prev.passed });
                    }
                  }
                  if (flips.length === 0) {
                    diffEl.innerHTML = '<div class="empty">No pass/fail changes since the previous run — stable.</div>';
                  } else {
                    const rows = flips.map(f => `
                      <tr class="${f.passed ? 'flip-pass' : 'flip-fail'}">
                        <td>${f.configName}</td>
                        <td>${f.caseId}</td>
                        <td>${f.previousPassed ? '<span class="pass">PASS</span>' : '<span class="fail">FAIL</span>'}
                          → ${f.passed ? '<span class="pass">PASS</span>' : '<span class="fail">FAIL</span>'}</td>
                        <td class="notes">${(f.notes || '').replace(/</g,'&lt;')}</td>
                      </tr>`).join('');
                    diffEl.innerHTML = `<table><thead><tr><th>Config</th><th>Case</th><th>Change</th><th>Notes</th></tr></thead><tbody>${rows}</tbody></table>`;
                  }
                }

                // ---------- trend charts ----------
                // Build one dataset per config, one data point per run.
                const labels = runs.map(r => r.timestamp.slice(0, 16).replace('T', ' '));
                const configs = Array.from(new Set(runs.flatMap(r => r.rows.map(x => x.configName))));

                const palette = ['#1565c0', '#2e7d32', '#ef6c00', '#6a1b9a', '#00838f', '#c62828', '#5d4037'];
                const passRateDatasets = configs.map((cfg, i) => ({
                  label: cfg,
                  data: runs.map(run => {
                    const cfgRows = run.rows.filter(r => r.configName === cfg);
                    return cfgRows.length === 0 ? null : passRate(cfgRows) * 100;
                  }),
                  borderColor: palette[i % palette.length],
                  backgroundColor: palette[i % palette.length],
                  tension: 0.2,
                  spanGaps: true,
                }));

                new Chart(document.getElementById('passRateChart'), {
                  type: 'line',
                  data: { labels, datasets: passRateDatasets },
                  options: {
                    responsive: true,
                    scales: { y: { min: 0, max: 100, title: { display: true, text: 'Pass rate (%)' } } },
                    plugins: { legend: { position: 'bottom' } },
                  }
                });

                const tokenDatasets = configs.map((cfg, i) => ({
                  label: cfg + ' (agent in+out)',
                  data: runs.map(run => {
                    const cfgRows = run.rows.filter(r => r.configName === cfg);
                    return cfgRows.reduce((s,r) => s + r.inputTokens + r.outputTokens, 0);
                  }),
                  borderColor: palette[i % palette.length],
                  backgroundColor: palette[i % palette.length],
                  tension: 0.2,
                }));

                new Chart(document.getElementById('tokenChart'), {
                  type: 'line',
                  data: { labels, datasets: tokenDatasets },
                  options: {
                    responsive: true,
                    scales: { y: { beginAtZero: true, title: { display: true, text: 'Agent tokens (per run total)' } } },
                    plugins: { legend: { position: 'bottom' } },
                  }
                });

                // ---------- detail table ----------
                const tbody = document.querySelector('#detail-table tbody');
                for (const r of latest.rows) {
                  const tr = document.createElement('tr');
                  tr.innerHTML = `
                    <td>${r.configName}</td>
                    <td>${r.caseId}</td>
                    <td class="${r.passed ? 'pass' : 'fail'}">${r.passed ? 'PASS' : 'FAIL'}</td>
                    <td>${r.causeCorrect ? '✓' : '✗'}</td>
                    <td class="num">${r.serviceCoverage.toFixed(2)}</td>
                    <td class="num">${r.iterations}</td>
                    <td class="num">${fmtInt(r.inputTokens)}/${fmtInt(r.outputTokens)}/${fmtInt(r.judgeTokens)}</td>
                    <td class="num">${r.durationSeconds.toFixed(1)}</td>
                    <td class="notes" title="${(r.notes || '').replace(/"/g,'&quot;')}">${(r.notes || '').replace(/</g,'&lt;')}</td>
                  `;
                  tbody.appendChild(tr);
                }
              </script>
            </body>
            </html>
            """;

        return template
            .Replace("{{GENERATED}}", generated)
            .Replace("{{RUN_COUNT}}", runs.Count.ToString(CultureInfo.InvariantCulture))
            .Replace("{{LATEST_FILE}}", latest.FileName)
            .Replace("{{DATA_JSON}}", json);
    }

    // ---- Data shapes used for serialisation into the HTML ----

    private sealed record RunData(
        string FileName,
        DateTime Timestamp,
        IReadOnlyList<RowData> Rows);

    private sealed record RowData(
        string ConfigName,
        string CaseId,
        bool Passed,
        bool CauseCorrect,
        double ServiceCoverage,
        int Iterations,
        int InputTokens,
        int OutputTokens,
        int JudgeTokens,
        double DurationSeconds,
        string Notes);
}
