using System.IO;
using System.Text;

namespace ConvertApp;

// MARK: - Block model

public abstract class DocBlock;

public sealed class ParagraphBlock : DocBlock
{
    public List<DocInline> Inlines { get; } = [];
}

public sealed class HeadingBlock : DocBlock
{
    public int Level { get; set; } = 1;
    public List<DocInline> Inlines { get; } = [];
}

public sealed class ListBlock : DocBlock
{
    public bool Ordered { get; set; }
    public List<ListEntry> Items { get; } = [];
}

public sealed class ListEntry
{
    public int Level { get; set; } = 1;
    public bool? Ordered { get; set; }
    public List<DocInline> Inlines { get; } = [];
}

public sealed class TableBlock : DocBlock
{
    public List<TableRow> Rows { get; } = [];
}

public sealed class TableRow
{
    public bool IsHeader { get; set; }
    public List<TableCell> Cells { get; } = [];
}

public sealed class TableCell
{
    public List<DocBlock> Blocks { get; } = [];
}

public sealed class CodeBlock : DocBlock
{
    public string Text { get; set; } = "";
}

public sealed class QuoteBlock : DocBlock
{
    public List<DocBlock> Blocks { get; } = [];
}

public sealed class RuleBlock : DocBlock;

public sealed class DocInline
{
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strike { get; set; }
    public bool Code { get; set; }
    public string? Link { get; set; }

    public bool SameStyle(DocInline other) =>
        Bold == other.Bold && Italic == other.Italic && Underline == other.Underline &&
        Strike == other.Strike && Code == other.Code && Link == other.Link;

    public DocInline WithText(string text) => new()
    {
        Text = text,
        Bold = Bold,
        Italic = Italic,
        Underline = Underline,
        Strike = Strike,
        Code = Code,
        Link = Link,
    };
}

// MARK: - Document

/// <summary>
/// A small rich-text model every document format is read into and written from.
/// Windows has no <c>textutil</c>, so this is what makes the document conversions possible
/// without requiring Word or LibreOffice.
/// </summary>
public sealed partial class DocModel
{
    public List<DocBlock> Blocks { get; } = [];

    public static DocModel Load(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (Documents.IsMarkdown(path))
            return HtmlDoc.Parse(Markdown.ToBody(ReadText(path)));

        if (Documents.IsHtml(path))
            return HtmlDoc.Parse(ReadText(path));

        switch (ext)
        {
            case ".docx":
            case ".docm":
                return DocxReader.Read(path);
            case ".odt":
                return OdtReader.Read(path);
            case ".rtf":
                return RtfReader.Read(Encoding.Latin1.GetString(File.ReadAllBytes(path)));
            default:
                return FromPlainText(ReadText(path));
        }
    }

    public static DocModel FromPlainText(string text)
    {
        var model = new DocModel();
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var paragraphs = normalized.Split("\n\n", StringSplitOptions.None);
        foreach (var raw in paragraphs)
        {
            var p = raw.Trim('\n');
            if (p.Trim().Length == 0) continue;
            var paragraph = new ParagraphBlock();
            paragraph.Inlines.Add(new DocInline { Text = p });
            model.Blocks.Add(paragraph);
        }
        return model;
    }

    /// <summary>Reads a file as UTF-8 when possible, falling back to Windows-1252 for old notes.</summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return WesternEuropean.GetString(bytes);
        }
    }

    private static Encoding? westernEuropean;

    public static Encoding WesternEuropean
    {
        get
        {
            if (westernEuropean is not null) return westernEuropean;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return westernEuropean = Encoding.GetEncoding(1252);
        }
    }

    // MARK: - HTML

    public string ToHtml(string title, bool forPrint = false, bool a4 = true) =>
        WrapHtml(RenderBlocks(Blocks), title, forPrint, a4);

    public static string WrapHtml(string body, string title, bool forPrint = false, bool a4 = true)
    {
        var page = a4 ? "A4" : "Letter";
        var printCss = forPrint ? $"@page {{ size: {page}; }} html, body {{ margin: 0; }}" : "";
        const string css = """
body { font-family: "Segoe UI Variable Text", "Segoe UI", Calibri, Arial, sans-serif; font-size: 11pt; line-height: 1.5; color: #1d1d1f; padding: 0.8in; max-width: 900px; }
h1 { font-size: 22pt; margin: 0 0 10pt; }
h2 { font-size: 16pt; margin: 18pt 0 8pt; }
h3 { font-size: 13pt; margin: 14pt 0 6pt; }
h4, h5, h6 { font-size: 11pt; margin: 12pt 0 4pt; }
p, ul, ol, blockquote, pre, table { margin: 0 0 9pt; }
code { font-family: Consolas, "Cascadia Mono", monospace; font-size: 9.5pt; background: #f2f2f4; padding: 0 2pt; }
pre { font-family: Consolas, "Cascadia Mono", monospace; font-size: 9.5pt; background: #f2f2f4; padding: 8pt 10pt; white-space: pre-wrap; }
blockquote { color: #555; margin-left: 12pt; padding-left: 10pt; border-left: 3pt solid #d0d0d6; }
table { border-collapse: collapse; width: 100%; }
th, td { border: 1px solid #d0d0d6; padding: 4pt 8pt; text-align: left; vertical-align: top; }
th { background: #f2f2f4; }
a { color: #0a66d8; }
hr { border: 0; border-top: 1px solid #d0d0d6; margin: 14pt 0; }
li { margin: 0 0 3pt; }
""";
        return "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>" + Markdown.Escape(title) +
               "</title>\n<style>\n" + printCss + "\n" + css +
               "</style></head><body>\n" + body + "\n</body></html>\n";
    }

    private static string RenderBlocks(IEnumerable<DocBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks) RenderBlock(block, sb);
        return sb.ToString();
    }

    private static void RenderBlock(DocBlock block, StringBuilder sb)
    {
        switch (block)
        {
            case HeadingBlock heading:
                sb.Append($"<h{heading.Level}>{RenderInlines(heading.Inlines)}</h{heading.Level}>\n");
                break;

            case ParagraphBlock paragraph:
                sb.Append("<p>").Append(RenderInlines(paragraph.Inlines)).Append("</p>\n");
                break;

            case CodeBlock code:
                sb.Append("<pre><code>").Append(Markdown.Escape(code.Text)).Append("</code></pre>\n");
                break;

            case QuoteBlock quote:
                sb.Append("<blockquote>\n").Append(RenderBlocks(quote.Blocks)).Append("</blockquote>\n");
                break;

            case RuleBlock:
                sb.Append("<hr>\n");
                break;

            case ListBlock list:
                var tag = list.Ordered ? "ol" : "ul";
                sb.Append($"<{tag}>\n");
                foreach (var item in list.Items)
                {
                    var own = item.Ordered ?? list.Ordered;
                    if (own != list.Ordered)
                    {
                        sb.Append($"<li style=\"list-style:none\"><{ (own ? "ol" : "ul") }>")
                          .Append("<li>").Append(RenderInlines(item.Inlines)).Append("</li>")
                          .Append($"</{(own ? "ol" : "ul")}></li>\n");
                    }
                    else
                    {
                        var indent = item.Level > 1 ? $" style=\"margin-left:{(item.Level - 1) * 18}pt\"" : "";
                        sb.Append($"<li{indent}>").Append(RenderInlines(item.Inlines)).Append("</li>\n");
                    }
                }
                sb.Append($"</{tag}>\n");
                break;

            case TableBlock table:
                sb.Append("<table>\n");
                var headerDone = false;
                foreach (var row in table.Rows)
                {
                    if (row.IsHeader && !headerDone)
                    {
                        headerDone = true;
                        sb.Append("<thead>");
                    }
                    sb.Append("<tr>");
                    foreach (var cell in row.Cells)
                    {
                        var cellTag = row.IsHeader ? "th" : "td";
                        sb.Append($"<{cellTag}>").Append(RenderBlocks(cell.Blocks)).Append($"</{cellTag}>");
                    }
                    sb.Append("</tr>\n");
                    if (row.IsHeader && headerDone) sb.Append("</thead>\n");
                }
                sb.Append("</table>\n");
                break;
        }
    }

    private static string RenderInlines(IEnumerable<DocInline> inlines)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            var text = Markdown.Escape(inline.Text).Replace("\n", "<br>\n");
            if (inline.Code) text = "<code>" + text + "</code>";
            if (inline.Bold) text = "<strong>" + text + "</strong>";
            if (inline.Italic) text = "<em>" + text + "</em>";
            if (inline.Underline) text = "<u>" + text + "</u>";
            if (inline.Strike) text = "<s>" + text + "</s>";
            if (inline.Link is { Length: > 0 } link)
                text = $"<a href=\"{Markdown.Escape(link)}\">{text}</a>";
            sb.Append(text);
        }
        return sb.ToString();
    }

    // MARK: - Plain text

    public string ToPlainText()
    {
        var sb = new StringBuilder();
        AppendPlain(Blocks, sb);
        return sb.ToString().TrimEnd() + "\n";
    }

    private static void AppendPlain(IEnumerable<DocBlock> blocks, StringBuilder sb)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    sb.Append(Plain(heading.Inlines)).Append("\n\n");
                    break;

                case ParagraphBlock paragraph:
                    sb.Append(Plain(paragraph.Inlines)).Append("\n\n");
                    break;

                case CodeBlock code:
                    sb.Append(code.Text).Append("\n\n");
                    break;

                case QuoteBlock quote:
                    var inner = new StringBuilder();
                    AppendPlain(quote.Blocks, inner);
                    foreach (var line in inner.ToString().Split('\n'))
                        if (line.Length > 0) sb.Append("> ").Append(line).Append('\n');
                    sb.Append('\n');
                    break;

                case RuleBlock:
                    sb.Append("---\n\n");
                    break;

                case ListBlock list:
                    var n = 1;
                    foreach (var item in list.Items)
                    {
                        var indent = new string(' ', Math.Max(0, item.Level - 1) * 2);
                        var ordered = item.Ordered ?? list.Ordered;
                        sb.Append(indent).Append(ordered ? $"{n++}. " : "• ").Append(Plain(item.Inlines)).Append('\n');
                    }
                    sb.Append('\n');
                    break;

                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        sb.Append(string.Join("\t", row.Cells.Select(c =>
                        {
                            var cell = new StringBuilder();
                            AppendPlain(c.Blocks, cell);
                            return cell.ToString().Trim().Replace("\n", " ");
                        }))).Append('\n');
                    }
                    sb.Append('\n');
                    break;
            }
        }
    }

    private static string Plain(IEnumerable<DocInline> inlines) => string.Concat(inlines.Select(i => i.Text));

    // MARK: - Branch formats

    public string ToRtf() => RtfWriter.Write(this);
    public void ToDocx(string path) => DocxWriter.Write(this, path);
    public void ToOdt(string path) => OdtWriter.Write(this, path);

    // MARK: - Helpers shared by readers

    internal static List<DocInline> TrimEdges(List<DocInline> inlines)
    {
        if (inlines.Count == 0) return inlines;
        var first = inlines[0];
        first.Text = first.Text.TrimStart();
        var last = inlines[^1];
        last.Text = last.Text.TrimEnd();
        inlines.RemoveAll(i => i.Text.Length == 0);
        return inlines;
    }
}
