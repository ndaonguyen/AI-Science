using System.Text;

namespace DistributedDebugger.Eval.Internal;

/// <summary>
/// Minimal CSV parser matching the dialect we emit in <c>EvalCommand.WriteCsvAsync</c>:
///
///   - Fields are comma-separated.
///   - A field is optionally wrapped in double quotes if it contains commas or newlines.
///   - Inside a quoted field, a literal double-quote is escaped as <c>""</c>.
///
/// Extracted as its own class so the parse logic is unit-testable in isolation
/// without touching the filesystem.
/// </summary>
internal static class CsvLineSplitter
{
    internal static List<string> Split(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    // Escaped double-quote inside a quoted field.
                    sb.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    sb.Append(ch);
                }
            }
            else
            {
                if (ch == ',')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else if (ch == '"' && sb.Length == 0)
                {
                    // Opening quote must be the first character of the field
                    // — mid-field quotes aren't something our writer produces,
                    // so we don't try to handle them.
                    inQuotes = true;
                }
                else
                {
                    sb.Append(ch);
                }
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
