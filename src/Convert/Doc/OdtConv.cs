using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace ConvertApp;

/// <summary>Reads and writes OpenDocument Text (.odt) files — a zip with XML inside.</summary>
public static class OdtReader
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    private sealed class TextStyle
    {
        public bool Bold, Italic, Underline, Strike;
    }

    public static DocModel Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var content = zip.GetEntry("content.xml") ?? throw new ConvertException("This ODT file has no content");
        XDocument document;
        using (var stream = content.Open()) document = XDocument.Load(stream);

        var styles = ReadStyles(document);
        var model = new DocModel();
        var body = document.Root?.Element(Office + "body")?.Element(Office + "text");
        if (body is null) return model;

        ParseBlocks(body, model.Blocks, styles);
        return model;
    }

    private static Dictionary<string, TextStyle> ReadStyles(XDocument document)
    {
        var raw = new Dictionary<string, (TextStyle Style, string? Parent)>();
        foreach (var style in document.Descendants(Style + "style"))
        {
            var name = (string?)style.Attribute(Style + "name");
            if (name is null) continue;
            var text = new TextStyle();
            var properties = style.Element(Style + "text-properties");
            if (properties is not null)
            {
                var weight = (string?)properties.Attribute(Fo + "font-weight");
                var posture = (string?)properties.Attribute(Fo + "font-style");
                var underline = (string?)properties.Attribute(Style + "text-underline-style");
                var strike = (string?)properties.Attribute(Style + "text-line-through-style");
                text.Bold = weight is "bold" or "600" or "700" or "800" or "900";
                text.Italic = posture == "italic";
                text.Underline = underline is not null and not "none";
                text.Strike = strike is not null and not "none";
            }
            raw[name] = (text, (string?)style.Attribute(Style + "parent-style-name"));
        }

        var resolved = new Dictionary<string, TextStyle>();
        TextStyle Resolve(string name, int depth = 0)
        {
            if (resolved.TryGetValue(name, out var done)) return done;
            if (!raw.TryGetValue(name, out var entry) || depth > 8) return new TextStyle();

            var result = new TextStyle
            {
                Bold = entry.Style.Bold,
                Italic = entry.Style.Italic,
                Underline = entry.Style.Underline,
                Strike = entry.Style.Strike,
            };
            if (entry.Parent is { } parent)
            {
                var inherited = Resolve(parent, depth + 1);
                result.Bold |= inherited.Bold;
                result.Italic |= inherited.Italic;
                result.Underline |= inherited.Underline;
                result.Strike |= inherited.Strike;
            }
            resolved[name] = result;
            return result;
        }

        foreach (var key in raw.Keys.ToList()) Resolve(key);
        return resolved;
    }

    private static void ParseBlocks(XElement parent, List<DocBlock> blocks, Dictionary<string, TextStyle> styles)
    {
        foreach (var element in parent.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "h":
                    var level = int.TryParse((string?)element.Attribute(Text + "outline-level"), out var l) ? Math.Clamp(l, 1, 6) : 1;
                    var heading = new HeadingBlock { Level = level };
                    ParseInlines(element, heading.Inlines, styles, new TextStyle());
                    DocModel.TrimEdges(heading.Inlines);
                    if (heading.Inlines.Count > 0) blocks.Add(heading);
                    break;

                case "p":
                    var paragraph = new ParagraphBlock();
                    ParseInlines(element, paragraph.Inlines, styles, new TextStyle());
                    DocModel.TrimEdges(paragraph.Inlines);
                    if (paragraph.Inlines.Count > 0) blocks.Add(paragraph);
                    break;

                case "list":
                    var list = ParseList(element, styles);
                    if (list.Items.Count > 0) blocks.Add(list);
                    break;

                case "table":
                    var table = ParseTable(element, styles);
                    if (table.Rows.Count > 0) blocks.Add(table);
                    break;

                case "section":
                case "text-box":
                    ParseBlocks(element, blocks, styles);
                    break;
            }
        }
    }

    private static ListBlock ParseList(XElement list, Dictionary<string, TextStyle> styles)
    {
        var block = new ListBlock();
        foreach (var item in list.Elements(Text + "list-item"))
        {
            foreach (var child in item.Elements())
            {
                if (child.Name.LocalName == "p")
                {
                    var entry = new ListEntry();
                    ParseInlines(child, entry.Inlines, styles, new TextStyle());
                    DocModel.TrimEdges(entry.Inlines);
                    if (entry.Inlines.Count > 0) block.Items.Add(entry);
                }
                else if (child.Name.LocalName == "list")
                {
                    var nested = ParseList(child, styles);
                    foreach (var entry in nested.Items)
                    {
                        entry.Level = 2;
                        entry.Ordered = nested.Ordered;
                        block.Items.Add(entry);
                    }
                }
            }
        }

        var styleName = (string?)list.Attribute(Text + "style-name");
        block.Ordered = styleName is not null && styleName.Contains("number", StringComparison.OrdinalIgnoreCase);
        return block;
    }

    private static TableBlock ParseTable(XElement table, Dictionary<string, TextStyle> styles)
    {
        var result = new TableBlock();
        foreach (var row in table.Elements(Table + "table-row"))
        {
            var tableRow = new TableRow();
            foreach (var cell in row.Elements(Table + "table-cell"))
            {
                var tableCell = new TableCell();
                foreach (var paragraph in cell.Elements(Text + "p"))
                {
                    var block = new ParagraphBlock();
                    ParseInlines(paragraph, block.Inlines, styles, new TextStyle());
                    DocModel.TrimEdges(block.Inlines);
                    if (block.Inlines.Count > 0) tableCell.Blocks.Add(block);
                }
                tableRow.Cells.Add(tableCell);
            }
            if (tableRow.Cells.Count > 0) result.Rows.Add(tableRow);
        }
        return result;
    }

    private static void ParseInlines(XElement parent, List<DocInline> output,
        Dictionary<string, TextStyle> styles, TextStyle current)
    {
        foreach (var node in parent.Nodes())
        {
            switch (node)
            {
                case XText text:
                    Append(output, text.Value, current);
                    break;

                case XElement element:
                    switch (element.Name.LocalName)
                    {
                        case "span":
                        case "a":
                        {
                            var style = new TextStyle
                            {
                                Bold = current.Bold, Italic = current.Italic,
                                Underline = current.Underline, Strike = current.Strike,
                            };
                            var styleName = (string?)element.Attribute(Text + "style-name");
                            if (styleName is not null && styles.TryGetValue(styleName, out var named))
                            {
                                style.Bold |= named.Bold;
                                style.Italic |= named.Italic;
                                style.Underline |= named.Underline;
                                style.Strike |= named.Strike;
                            }
                            var before = output.Count;
                            ParseInlines(element, output, styles, style);
                            if (element.Name.LocalName == "a")
                            {
                                var href = (string?)element.Attribute(XLink + "href");
                                if (href is not null)
                                    for (var i = before; i < output.Count; i++) output[i].Link ??= href;
                            }
                            break;
                        }

                        case "s":
                        {
                            var count = int.TryParse((string?)element.Attribute(Text + "c"), out var c) ? c : 1;
                            Append(output, new string(' ', Math.Clamp(count, 1, 100)), current);
                            break;
                        }

                        case "tab": Append(output, "\t", current); break;
                        case "line-break": Append(output, "\n", current); break;
                        case "note":
                        case "annotation":
                        case "tracked-changes":
                        case "soft-page-break":
                            break;
                        default:
                            ParseInlines(element, output, styles, current);
                            break;
                    }
                    break;
            }
        }
    }

    private static void Append(List<DocInline> output, string text, TextStyle style)
    {
        if (text.Length == 0) return;
        if (output.Count > 0 &&
            output[^1].Bold == style.Bold && output[^1].Italic == style.Italic &&
            output[^1].Underline == style.Underline && output[^1].Strike == style.Strike)
        {
            output[^1].Text += text;
            return;
        }
        output.Add(new DocInline
        {
            Text = text,
            Bold = style.Bold,
            Italic = style.Italic,
            Underline = style.Underline,
            Strike = style.Strike,
        });
    }
}

public static class OdtWriter
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    public static void Write(DocModel model, string path)
    {
        var styles = new List<XElement>();
        var styleNames = new Dictionary<string, string>();
        var styleCounter = 0;

        string StyleFor(DocInline inline)
        {
            var key = $"{inline.Bold}|{inline.Italic}|{inline.Underline}|{inline.Strike}";
            if (styleNames.TryGetValue(key, out var existing)) return existing;

            var name = "T" + (++styleCounter);
            var properties = new List<XAttribute>();
            if (inline.Bold) properties.Add(new XAttribute(Fo + "font-weight", "bold"));
            if (inline.Italic) properties.Add(new XAttribute(Fo + "font-style", "italic"));
            if (inline.Underline) properties.Add(new XAttribute(Style + "text-underline-style", "solid"));
            if (inline.Strike) properties.Add(new XAttribute(Style + "text-line-through-style", "solid"));

            styles.Add(new XElement(Style + "style",
                new XAttribute(Style + "name", name),
                new XAttribute(Style + "family", "text"),
                new XElement(Style + "text-properties", properties.Cast<object>().ToArray())));
            styleNames[key] = name;
            return name;
        }

        var body = new XElement(Office + "text");
        foreach (var block in model.Blocks) AppendBlock(block, body, StyleFor);

        var content = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Office + "document-content",
                new XAttribute(XNamespace.Xmlns + "office", Office),
                new XAttribute(XNamespace.Xmlns + "text", Text),
                new XAttribute(XNamespace.Xmlns + "style", Style),
                new XAttribute(XNamespace.Xmlns + "fo", Fo),
                new XAttribute(XNamespace.Xmlns + "table", Table),
                new XAttribute(Office + "version", "1.2"),
                new XElement(Office + "automatic-styles", styles),
                new XElement(Office + "body", body)));

        var manifest = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Manifest + "manifest",
                new XAttribute(XNamespace.Xmlns + "manifest", Manifest),
                new XAttribute(Manifest + "version", "1.2"),
                new XElement(Manifest + "file-entry",
                    new XAttribute(Manifest + "full-path", "/"),
                    new XAttribute(Manifest + "media-type", "application/vnd.oasis.opendocument.text")),
                new XElement(Manifest + "file-entry",
                    new XAttribute(Manifest + "full-path", "content.xml"),
                    new XAttribute(Manifest + "media-type", "text/xml")),
                new XElement(Manifest + "file-entry",
                    new XAttribute(Manifest + "full-path", "styles.xml"),
                    new XAttribute(Manifest + "media-type", "text/xml"))));

        var stylesXml = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Office + "document-styles",
                new XAttribute(XNamespace.Xmlns + "office", Office),
                new XAttribute(XNamespace.Xmlns + "style", Style),
                new XAttribute(XNamespace.Xmlns + "fo", Fo),
                new XAttribute(Office + "version", "1.2"),
                new XElement(Office + "styles",
                    new XElement(Text + "list-style",
                        new XAttribute(Style + "name", "Bulleted"),
                        new XElement(Text + "list-level-style-bullet",
                            new XAttribute(Text + "level", "1"),
                            new XAttribute(Text + "bullet-char", "•"))),
                    new XElement(Text + "list-style",
                        new XAttribute(Style + "name", "Numbered"),
                        new XElement(Text + "list-level-style-number",
                            new XAttribute(Text + "level", "1"),
                            new XAttribute(Style + "num-format", "1"),
                            new XAttribute(Style + "num-suffix", ".")))),
                new XElement(Office + "automatic-styles")));

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        // The ODT spec wants "mimetype" first and stored uncompressed.
        var mimetype = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(mimetype.Open(), new UTF8Encoding(false)))
            writer.Write("application/vnd.oasis.opendocument.text");

        WriteEntry(zip, "content.xml", content);
        WriteEntry(zip, "styles.xml", stylesXml);
        WriteEntry(zip, "META-INF/manifest.xml", manifest);
    }

    private static void WriteEntry(ZipArchive zip, string name, XDocument document)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        document.Save(writer, SaveOptions.DisableFormatting);
    }

    private static void AppendBlock(DocBlock block, XElement body, Func<DocInline, string> styleFor)
    {
        switch (block)
        {
            case HeadingBlock heading:
                body.Add(new XElement(Text + "h",
                    new XAttribute(Text + "outline-level", heading.Level),
                    Inlines(heading.Inlines, styleFor)));
                break;

            case ParagraphBlock paragraph:
                body.Add(new XElement(Text + "p", Inlines(paragraph.Inlines, styleFor)));
                break;

            case CodeBlock code:
                foreach (var line in code.Text.Replace("\r\n", "\n").Split('\n'))
                    body.Add(new XElement(Text + "p",
                        new XAttribute(Style + "style-name", "Preformatted_20_Text"), line));
                break;

            case RuleBlock:
                body.Add(new XElement(Text + "p", "—"));
                break;

            case QuoteBlock quote:
                foreach (var inner in quote.Blocks)
                    AppendBlock(inner, body, styleFor);
                break;

            case ListBlock list:
                var items = new XElement(Text + "list",
                    new XAttribute(Text + "style-name", list.Ordered ? "Numbered" : "Bulleted"));
                foreach (var item in list.Items)
                {
                    var paragraph = new XElement(Text + "p", Inlines(item.Inlines, styleFor));
                    items.Add(new XElement(Text + "list-item", paragraph));
                }
                body.Add(items);
                break;

            case TableBlock table:
                var element = new XElement(Table + "table",
                    new XAttribute(Table + "name", "Table" + Guid.NewGuid().ToString("N")[..6]));
                foreach (var row in table.Rows)
                {
                    var rowElement = new XElement(Table + "table-row");
                    foreach (var cell in row.Cells)
                    {
                        var cellElement = new XElement(Table + "table-cell");
                        foreach (var inner in cell.Blocks)
                        {
                            if (inner is ParagraphBlock cp)
                                cellElement.Add(new XElement(Text + "p", Inlines(cp.Inlines, styleFor)));
                        }
                        if (!cellElement.HasElements) cellElement.Add(new XElement(Text + "p"));
                        rowElement.Add(cellElement);
                    }
                    element.Add(rowElement);
                }
                body.Add(element);
                break;
        }
    }

    private static IEnumerable<object> Inlines(IEnumerable<DocInline> inlines, Func<DocInline, string> styleFor)
    {
        foreach (var inline in inlines)
        {
            var pieces = inline.Text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < pieces.Length; i++)
            {
                if (i > 0) yield return new XElement(Text + "line-break");
                if (pieces[i].Length == 0) continue;
                var styled = inline.Bold || inline.Italic || inline.Underline || inline.Strike;
                object run = styled
                    ? new XElement(Text + "span", new XAttribute(Text + "style-name", styleFor(inline)), pieces[i])
                    : pieces[i];
                yield return run;
            }
        }
    }
}
