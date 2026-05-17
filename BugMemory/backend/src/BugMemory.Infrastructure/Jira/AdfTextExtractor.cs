using System.Text;
using System.Text.Json;

namespace BugMemory.Infrastructure.Jira;

/// <summary>
/// Jira Cloud REST API v3 returns rich-text fields (description, comment
/// body) as Atlassian Document Format (ADF) — a tree of typed nodes
/// serialized as JSON. We don't need rendering fidelity; we just need
/// the textual content for the LLM to read.
///
/// This walker recursively pulls text out of every node's "text"
/// property and joins paragraph/listItem nodes with newlines. Links
/// show up as the link text, not the URL — that's a deliberate choice
/// to keep snippets readable. URLs in the description body that are
/// referenced as bare URLs (not link nodes) come through unchanged.
///
/// Shape we expect (simplified):
///   { "type": "doc", "content": [
///       { "type": "paragraph", "content": [
///           { "type": "text", "text": "Hello " },
///           { "type": "text", "text": "world", "marks": [...] }
///       ]},
///       { "type": "bulletList", "content": [
///           { "type": "listItem", "content": [...] }
///       ]}
///   ]}
///
/// If we encounter an unknown node type with a "content" array, we
/// recurse — that's resilient to future ADF additions. Unknown leaf
/// nodes (no "content", no "text") get skipped silently.
/// </summary>
internal static class AdfTextExtractor
{
    public static string Flatten(JsonElement? root)
    {
        if (root is null || root.Value.ValueKind == JsonValueKind.Null
                         || root.Value.ValueKind == JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        Walk(root.Value, sb);
        return sb.ToString().Trim();
    }

    private static void Walk(JsonElement node, StringBuilder sb)
    {
        if (node.ValueKind != JsonValueKind.Object) return;

        // Block-level nodes that should produce a line break after their content.
        // Keep this list intentionally minimal — too many block types just makes
        // the output look noisy with extra blank lines.
        var type = node.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString() ?? ""
            : "";

        // Leaf text node — emit and return.
        if (type == "text" && node.TryGetProperty("text", out var textProp)
                           && textProp.ValueKind == JsonValueKind.String)
        {
            sb.Append(textProp.GetString());
            return;
        }

        // Hard break and inline code marks etc — emit a space to separate
        // tokens. Not worth deeper formatting for the LLM's purposes.
        if (type == "hardBreak")
        {
            sb.Append('\n');
            return;
        }

        // For anything with a "content" array, recurse into it.
        if (node.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in content.EnumerateArray())
            {
                Walk(child, sb);
            }
        }

        // Block-finishing newlines. Done AFTER recursion so trailing nodes
        // get terminated. Limited to common block types.
        switch (type)
        {
            case "paragraph":
            case "heading":
            case "listItem":
            case "codeBlock":
            case "blockquote":
                if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                break;
        }
    }
}
