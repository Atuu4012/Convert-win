using System.Text;
using System.Text.RegularExpressions;

namespace ConvertApp;

/// <summary>
/// A small Markdown → HTML renderer: headings, paragraphs, bold, italic, strike, inline code,
/// fenced code blocks, links, images (as alt text), block quotes, nested lists, task lists,
/// tables and rules. The HTML then goes through the same pipeline as any HTML file.
/// </summary>
public static partial class Markdown
{
    public static string ToBody(string source)
    {
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new List<string>();
        var paragraph = new List<string>();
        var listStack = new List<(string Tag, int Indent)>();
        var i = 0;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            output.Add("<p>" + string.Join("<br>\n", paragraph.Select(Inline)) + "</p>");
            paragraph.Clear();
        }

        void CloseLists(int depth = 0)
        {
            while (listStack.Count > depth)
            {
                var last = listStack[^1];
                listStack.RemoveAt(listStack.Count - 1);
                output.Add($"</li></{last.Tag}>");
            }
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            var indent = IndentOf(line);

            // Fenced code block
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                CloseLists();
                var fence = trimmed[..3];
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith(fence, StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }
                output.Add("<pre><code>" + string.Join("\n", code) + "</code></pre>");
                i++;
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                if (listStack.Count > 0)
                {
                    string? next = null;
                    for (var j = i + 1; j < lines.Length; j++)
                    {
                        if (lines[j].Trim().Length > 0) { next = lines[j]; break; }
                    }
                    if (next is null || ListItem(next) is null) CloseLists();
                }
                i++;
                continue;
            }

            // Heading
            var heading = Regex.Match(trimmed, @"^(#{1,6})\s+(.*?)\s*#*\s*$");
            if (heading.Success)
            {
                FlushParagraph();
                CloseLists();
                var level = heading.Groups[1].Value.Length;
                output.Add($"<h{level}>{Inline(heading.Groups[2].Value)}</h{level}>");
                i++;
                continue;
            }

            // Horizontal rule
            if (Regex.IsMatch(trimmed, @"^([-*_])(\s*\1){2,}$"))
            {
                FlushParagraph();
                CloseLists();
                output.Add("<hr>");
                i++;
                continue;
            }

            // Table: header row, then a |---|---| separator
            if (trimmed.Contains('|') && i + 1 < lines.Length &&
                Regex.IsMatch(lines[i + 1].Trim(), @"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$"))
            {
                FlushParagraph();
                CloseLists();
                var rows = new List<string>
                {
                    "<tr>" + string.Concat(Cells(trimmed).Select(c => $"<th>{Inline(c)}</th>")) + "</tr>",
                };
                i += 2;
                while (i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0)
                {
                    rows.Add("<tr>" + string.Concat(Cells(lines[i]).Select(c => $"<td>{Inline(c)}</td>")) + "</tr>");
                    i++;
                }
                output.Add("<table>" + string.Concat(rows) + "</table>");
                continue;
            }

            // Block quote (consecutive "> " lines, rendered recursively)
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                CloseLists();
                var quoted = new List<string>();
                while (i < lines.Length)
                {
                    var t = lines[i].Trim();
                    if (!t.StartsWith('>')) break;
                    var rest = t[1..];
                    if (rest.StartsWith(' ')) rest = rest[1..];
                    quoted.Add(rest);
                    i++;
                }
                output.Add("<blockquote>" + ToBody(string.Join("\n", quoted)) + "</blockquote>");
                continue;
            }

            // List item
            var item = ListItem(line);
            if (item is not null)
            {
                FlushParagraph();
                if (listStack.Count == 0 || indent > listStack[^1].Indent)
                {
                    output.Add($"<{item.Value.Tag}>");
                    listStack.Add((item.Value.Tag, indent));
                }
                else
                {
                    while (listStack.Count > 1 && indent < listStack[^1].Indent)
                    {
                        var last = listStack[^1];
                        listStack.RemoveAt(listStack.Count - 1);
                        output.Add($"</li></{last.Tag}>");
                    }
                    if (listStack[^1].Tag != item.Value.Tag)
                    {
                        var last = listStack[^1];
                        listStack.RemoveAt(listStack.Count - 1);
                        output.Add($"</li></{last.Tag}><{item.Value.Tag}>");
                        listStack.Add((item.Value.Tag, indent));
                    }
                    else
                    {
                        output.Add("</li>");
                    }
                }

                var content = item.Value.Text;
                var task = Regex.Match(content, @"^\[([ xX])\]\s+(.*)$");
                if (task.Success) content = (task.Groups[1].Value == " " ? "☐ " : "☑ ") + task.Groups[2].Value;
                output.Add("<li>" + Inline(content));
                i++;
                continue;
            }

            // Continuation of a list item's text
            if (listStack.Count > 0 && indent > 0)
            {
                output.Add(" " + Inline(trimmed));
                i++;
                continue;
            }

            CloseLists();
            paragraph.Add(trimmed);
            i++;
        }

        FlushParagraph();
        CloseLists();
        return string.Join("\n", output);
    }

    /// <summary>A complete HTML document with the same typography as other converted documents.</summary>
    public static string ToStandaloneHtml(string source, string title) =>
        DocModel.WrapHtml(ToBody(source), title);

    private static int IndentOf(string line)
    {
        var n = 0;
        foreach (var c in line)
        {
            if (c == '\t') n += 4;
            else if (c == ' ') n++;
            else break;
        }
        return n;
    }

    private static (string Tag, string Text)? ListItem(string line)
    {
        var t = line.Trim();
        var bullet = Regex.Match(t, @"^[-*+]\s+(.*)$");
        if (bullet.Success) return ("ul", bullet.Groups[1].Value);
        var numbered = Regex.Match(t, @"^\d{1,9}[.)]\s+(.*)$");
        if (numbered.Success) return ("ol", numbered.Groups[1].Value);
        return null;
    }

    private static IEnumerable<string> Cells(string row)
    {
        var t = row.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(c => c.Trim());
    }

    // MARK: - Inline

    public static string Inline(string text)
    {
        // Pull code spans out first so nothing inside them is formatted.
        var codes = new List<string>();
        var s = Regex.Replace(text, @"`([^`]+)`", m =>
        {
            codes.Add("<code>" + Escape(m.Groups[1].Value) + "</code>");
            return "\uE000" + (codes.Count - 1) + "\uE000";
        });

        s = Escape(s);
        s = Regex.Replace(s, @"!\[([^\]]*)\]\(([^)\s]+)(?:\s+&quot;[^&]*&quot;)?\)", m => m.Groups[1].Value);
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)\s]+)(?:\s+&quot;[^&]*&quot;)?\)",
            m => $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>");
        s = Regex.Replace(s, @"&lt;(https?://[^\s&]+)&gt;",
            m => $"<a href=\"{m.Groups[1].Value}\">{m.Groups[1].Value}</a>");
        s = Regex.Replace(s, @"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", m => $"<strong>{m.Groups[2].Value}</strong>");
        s = Regex.Replace(s, @"(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?![\w*])", m => $"<em>{m.Groups[1].Value}</em>");
        s = Regex.Replace(s, @"(?<![\w_])_(?=\S)(.+?)(?<=\S)_(?![\w_])", m => $"<em>{m.Groups[1].Value}</em>");
        s = Regex.Replace(s, @"~~(?=\S)(.+?)(?<=\S)~~", m => $"<del>{m.Groups[1].Value}</del>");
        s = Regex.Replace(s, "\uE000(\\d+)\uE000", m => codes[int.Parse(m.Groups[1].Value)]);
        return s;
    }

    public static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
