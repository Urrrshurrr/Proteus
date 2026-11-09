using System.Text;

namespace Proteus.Core.Engine;

/// <summary>Small CSV reader/writer (RFC 4180 quoting; comma, semicolon or tab separated).</summary>
public static class Csv
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrEmpty(text)) return rows;
        char sep = DetectSeparator(text);
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0) quoted = true;
            else if (c == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n')
            {
                row.Add(field.ToString()); field.Clear();
                if (row.Any(f => f.Length > 0)) rows.Add(row.ToArray());
                row.Clear();
            }
            else field.Append(c);
        }
        row.Add(field.ToString());
        if (row.Any(f => f.Length > 0)) rows.Add(row.ToArray());
        return rows;
    }

    private static char DetectSeparator(string text)
    {
        var firstLine = text.Split('\n')[0];
        int tabs = firstLine.Count(c => c == '\t'), semis = firstLine.Count(c => c == ';'), commas = firstLine.Count(c => c == ',');
        if (tabs > 0 && tabs >= commas) return '\t';
        if (semis > commas) return ';';
        return ',';
    }

    public static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r', ';']) >= 0 || (value.Length > 0 && (value[0] == ' ' || value[^1] == ' '))
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    public static string Write(IEnumerable<IEnumerable<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows) sb.Append(string.Join(",", r.Select(Escape))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Reads a rename list: each row is "current file (full path or name), new name".
    /// A first row that looks like a header is skipped.
    /// </summary>
    public static List<(string Current, string New)> ReadRenameList(string text)
    {
        var result = new List<(string, string)>();
        var rows = Parse(text);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Length < 2) continue;
            var a = r[0].Trim();
            var b = r[1].Trim();
            if (a.Length == 0 || b.Length == 0) continue;
            if (i == 0 && IsHeader(a) && !File.Exists(a)) continue;
            result.Add((a, b));
        }
        return result;
    }

    private static bool IsHeader(string s) => s.ToLowerInvariant() switch
    {
        "old" or "old name" or "current" or "current name" or "current path" or "source" or "from" or "file" or "path"
            or "original" or "name" or "filename" => true,
        _ => false,
    };
}
