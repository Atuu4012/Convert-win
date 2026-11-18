using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ConvertApp;

/// <summary>Reads and writes Word (.docx) files with the Open XML SDK.</summary>
public static class DocxReader
{
    public static DocModel Read(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var main = document.MainDocumentPart;
        var body = main?.Document?.Body;
        if (body is null) throw new ConvertException("This Word file is empty or damaged");

        var orderedNumbering = ReadNumbering(main);
        var model = new DocModel();
        ListBlock? currentList = null;

        foreach (var element in body.ChildElements)
        {
            switch (element)
            {
                case W.Paragraph paragraph:
                    var properties = paragraph.ParagraphProperties;
                    var headingLevel = HeadingLevel(properties);

                    if (properties?.NumberingProperties?.NumberingId?.Val?.Value is { } numberingId)
                    {
                        var ordered = orderedNumbering.TryGetValue(numberingId, out var o) && o;
                        if (currentList is null || currentList.Ordered != ordered)
                        {
                            currentList = new ListBlock { Ordered = ordered };
                            model.Blocks.Add(currentList);
                        }
                        var entry = new ListEntry { Level = ListLevel(properties) };
                        ParseInlines(paragraph, entry.Inlines, main, new InlineStyle());
                        DocModel.TrimEdges(entry.Inlines);
                        if (entry.Inlines.Count > 0) currentList.Items.Add(entry);
                        break;
                    }

                    currentList = null;
                    if (headingLevel is { } level)
                    {
                        var heading = new HeadingBlock { Level = level };
                        ParseInlines(paragraph, heading.Inlines, main, new InlineStyle());
                        model.Blocks.Add(heading);
                    }
                    else
                    {
                        var block = new ParagraphBlock();
                        ParseInlines(paragraph, block.Inlines, main, new InlineStyle());
                        if (block.Inlines.Count > 0) model.Blocks.Add(block);
                    }
                    break;

                case W.Table table:
                    currentList = null;
                    model.Blocks.Add(ParseTable(table, main, orderedNumbering));
                    break;
            }
        }

        return model;
    }

    private sealed class InlineStyle
    {
        public bool Bold, Italic, Underline, Strike, Code;
        public string? Link;
        public InlineStyle Copy() => (InlineStyle)MemberwiseClone();
    }

    private static void ParseInlines(OpenXmlElement parent, List<DocInline> output,
        MainDocumentPart? main, InlineStyle style)
    {
        foreach (var child in parent.ChildElements)
        {
            switch (child)
            {
                case W.Run run:
                    var runStyle = RunStyle(run, style);
                    foreach (var piece in run.ChildElements)
                    {
                        switch (piece)
                        {
                            case W.Text text:
                                Append(output, text.Text, runStyle);
                                break;
                            case W.Break:
                                Append(output, "\n", runStyle);
                                break;
                            case W.TabChar:
                                Append(output, "\t", runStyle);
                                break;
                            case W.NoBreakHyphen:
                                Append(output, "-", runStyle);
                                break;
                        }
                    }
                    break;

                case W.Hyperlink hyperlink:
                    var id = hyperlink.Id?.Value;
                    var url = id is null ? null : main?.HyperlinkRelationships
                        .FirstOrDefault(r => r.Id == id)?.Uri.ToString();
                    var linked = style.Copy();
                    if (url is not null) linked.Link = url;
                    ParseInlines(hyperlink, output, main, linked);
                    break;

                case W.SimpleField field:
                    ParseInlines(field, output, main, style);
                    break;

                case W.InsertedRun or W.DeletedRun or W.SdtContentRun:
                    ParseInlines(child, output, main, style);
                    break;

                case W.Paragraph paragraph:
                    // Text box or similar nested paragraph.
                    ParseInlines(paragraph, output, main, style);
                    Append(output, "\n", style);
                    break;
            }
        }
    }

    private static InlineStyle RunStyle(W.Run run, InlineStyle style)
    {
        var properties = run.RunProperties;
        if (properties is null) return style;
        var copy = style.Copy();
        if (properties.Bold is { } bold) copy.Bold = bold.Val?.Value != false;
        if (properties.Italic is { } italic) copy.Italic = italic.Val?.Value != false;
        if (properties.Strike is { } strike) copy.Strike = strike.Val?.Value != false;
        if (properties.Underline?.Val?.Value is { } underline)
            copy.Underline = underline != UnderlineValues.None && underline.ToString() != "Nil";
        if (properties.RunStyle?.Val?.Value is { } styleId && styleId.Contains("Code", StringComparison.OrdinalIgnoreCase))
            copy.Code = true;
        return copy;
    }

    private static void Append(List<DocInline> output, string text, InlineStyle style)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (output.Count > 0 && SameStyle(output[^1], style))
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
            Code = style.Code,
            Link = style.Link,
        });
    }

    private static bool SameStyle(DocInline inline, InlineStyle style) =>
        inline.Bold == style.Bold && inline.Italic == style.Italic &&
        inline.Underline == style.Underline && inline.Strike == style.Strike &&
        inline.Code == style.Code && inline.Link == style.Link;

    private static int? HeadingLevel(W.ParagraphProperties? properties)
    {
        var styleId = properties?.ParagraphStyleId?.Val?.Value;
        if (!string.IsNullOrEmpty(styleId))
        {
            var match = System.Text.RegularExpressions.Regex.Match(styleId, @"[Hh]eading\s*(\d)");
            if (match.Success) return Math.Clamp(int.Parse(match.Groups[1].Value), 1, 6);
            if (styleId.Contains("Title", StringComparison.OrdinalIgnoreCase)) return 1;
        }
        if (properties?.OutlineLevel?.Val?.Value is { } outline)
            return Math.Clamp(outline + 1, 1, 6);
        return null;
    }

    private static int ListLevel(W.ParagraphProperties? properties) =>
        Math.Clamp((properties?.NumberingProperties?.NumberingLevelReference?.Val?.Value ?? 0) + 1, 1, 9);

    private static Dictionary<int, bool> ReadNumbering(MainDocumentPart? main)
    {
        var result = new Dictionary<int, bool>();
        var numbering = main?.NumberingDefinitionsPart?.Numbering;
        if (numbering is null) return result;

        var abstractOrdered = new Dictionary<int, bool>();
        foreach (var abstractNum in numbering.Elements<AbstractNum>())
        {
            var id = abstractNum.AbstractNumberId?.Value;
            if (id is null) continue;
            var level = abstractNum.Elements<Level>().FirstOrDefault();
            var format = level?.NumberingFormat?.Val?.Value;
            abstractOrdered[id.Value] = format is not null && format != NumberFormatValues.Bullet;
        }

        foreach (var instance in numbering.Elements<NumberingInstance>())
        {
            var id = instance.NumberID?.Value;
            var abstractId = instance.AbstractNumId?.Val?.Value;
            if (id is null || abstractId is null) continue;
            result[id.Value] = abstractOrdered.TryGetValue(abstractId.Value, out var ordered) && ordered;
        }
        return result;
    }

    private static TableBlock ParseTable(W.Table table, MainDocumentPart? main, Dictionary<int, bool> numbering)
    {
        var result = new TableBlock();
        foreach (var row in table.Elements<W.TableRow>())
        {
            var tableRow = new TableRow { IsHeader = row.TableRowProperties?.GetFirstChild<TableHeader>() is not null };
            foreach (var cell in row.Elements<W.TableCell>())
            {
                var tableCell = new TableCell();
                foreach (var element in cell.ChildElements)
                {
                    switch (element)
                    {
                        case W.Paragraph paragraph:
                            var block = new ParagraphBlock();
                            ParseInlines(paragraph, block.Inlines, main, new InlineStyle());
                            if (block.Inlines.Count > 0) tableCell.Blocks.Add(block);
                            break;
                        case W.Table nested:
                            tableCell.Blocks.Add(ParseTable(nested, main, numbering));
                            break;
                    }
                }
                tableRow.Cells.Add(tableCell);
            }
            if (tableRow.Cells.Count > 0) result.Rows.Add(tableRow);
        }
        return result;
    }
}

public static class DocxWriter
{
    public static void Write(DocModel model, string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new W.Document();
        AddStyles(main);

        var body = new W.Body();
        foreach (var block in model.Blocks) AppendBlock(block, body);
        if (body.ChildElements.Count == 0) body.Append(new W.Paragraph());
        main.Document.Append(body);
        main.Document.Save();
    }

    private static void AppendBlock(DocBlock block, W.Body body)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var headingParagraph = new W.Paragraph(new W.ParagraphProperties(
                    new W.ParagraphStyleId { Val = "Heading" + Math.Clamp(heading.Level, 1, 6) }));
                AppendInlines(heading.Inlines, headingParagraph);
                body.Append(headingParagraph);
                break;

            case ParagraphBlock paragraph:
                var p = new W.Paragraph();
                AppendInlines(paragraph.Inlines, p);
                body.Append(p);
                break;

            case CodeBlock code:
                foreach (var line in code.Text.Replace("\r\n", "\n").Split('\n'))
                {
                    var cp = new W.Paragraph(new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Code" }));
                    if (line.Length > 0)
                        cp.Append(new W.Run(new W.Text(line) { Space = SpaceProcessingModeValues.Preserve }));
                    body.Append(cp);
                }
                break;

            case RuleBlock:
                body.Append(new W.Paragraph(new W.ParagraphProperties(
                    new W.ParagraphBorders(new W.BottomBorder { Val = BorderValues.Single, Size = 6 }))));
                break;

            case QuoteBlock quote:
                foreach (var inner in quote.Blocks)
                {
                    if (inner is ParagraphBlock qp)
                    {
                        var paragraph = new W.Paragraph(new W.ParagraphProperties(
                            new W.Indentation { Left = "720" },
                            new W.ParagraphBorders(new W.LeftBorder { Val = BorderValues.Single, Size = 12 })));
                        foreach (var inline in qp.Inlines)
                            paragraph.Append(new W.Run(new W.RunProperties(new W.Italic()),
                                new W.Text(inline.Text) { Space = SpaceProcessingModeValues.Preserve }));
                        body.Append(paragraph);
                    }
                    else AppendBlock(inner, body);
                }
                break;

            case ListBlock list:
                var number = 1;
                foreach (var item in list.Items)
                {
                    var ordered = item.Ordered ?? list.Ordered;
                    var paragraph = new W.Paragraph(new W.ParagraphProperties(
                        new W.Indentation { Left = (720 * Math.Max(1, item.Level)).ToString(), Hanging = "360" }));
                    paragraph.Append(new W.Run(new W.Text(ordered ? $"{number++}. " : "• ")
                    {
                        Space = SpaceProcessingModeValues.Preserve,
                    }));
                    AppendInlines(item.Inlines, paragraph);
                    body.Append(paragraph);
                }
                break;

            case TableBlock table:
                var wTable = new W.Table(new W.TableProperties(
                    new W.TableBorders(
                        new W.TopBorder { Val = BorderValues.Single, Size = 4 },
                        new W.BottomBorder { Val = BorderValues.Single, Size = 4 },
                        new W.LeftBorder { Val = BorderValues.Single, Size = 4 },
                        new W.RightBorder { Val = BorderValues.Single, Size = 4 },
                        new W.InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                        new W.InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
                    new W.TableWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" }));

                foreach (var row in table.Rows)
                {
                    var wRow = new W.TableRow();
                    foreach (var cell in row.Cells)
                    {
                        var wCell = new W.TableCell(new W.TableCellProperties(
                            new W.Shading { Fill = row.IsHeader ? "F2F2F4" : "FFFFFF", Val = ShadingPatternValues.Clear }));
                        foreach (var inner in cell.Blocks)
                        {
                            if (inner is ParagraphBlock cp)
                            {
                                var paragraph = new W.Paragraph();
                                foreach (var inline in cp.Inlines)
                                {
                                    var runProperties = new W.RunProperties();
                                    if (row.IsHeader) runProperties.Append(new W.Bold());
                                    var run = new W.Run();
                                    if (runProperties.HasChildren) run.Append(runProperties);
                                    run.Append(new W.Text(inline.Text) { Space = SpaceProcessingModeValues.Preserve });
                                    paragraph.Append(run);
                                }
                                wCell.Append(paragraph);
                            }
                            else
                            {
                                var paragraph = new W.Paragraph(new W.Run(
                                    new W.Text(Plain(inner)) { Space = SpaceProcessingModeValues.Preserve }));
                                wCell.Append(paragraph);
                            }
                        }
                        if (wCell.Elements<W.Paragraph>().Count() == 0) wCell.Append(new W.Paragraph());
                        wRow.Append(wCell);
                    }
                    wTable.Append(wRow);
                }
                body.Append(wTable);
                break;
        }
    }

    private static string Plain(DocBlock block) => block switch
    {
        ParagraphBlock paragraph => string.Concat(paragraph.Inlines.Select(i => i.Text)),
        HeadingBlock heading => string.Concat(heading.Inlines.Select(i => i.Text)),
        CodeBlock code => code.Text,
        _ => "",
    };

    private static void AppendInlines(IEnumerable<DocInline> inlines, W.Paragraph paragraph)
    {
        foreach (var inline in inlines)
        {
            var properties = new W.RunProperties();
            if (inline.Bold) properties.Append(new W.Bold());
            if (inline.Italic) properties.Append(new W.Italic());
            if (inline.Underline) properties.Append(new W.Underline { Val = W.UnderlineValues.Single });
            if (inline.Strike) properties.Append(new W.Strike());
            if (inline.Code) properties.Append(new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });

            var pieces = inline.Text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < pieces.Length; i++)
            {
                if (i > 0) paragraph.Append(new W.Run(new W.Break()));
                if (pieces[i].Length == 0 && pieces.Length > 1) continue;
                var run = new W.Run();
                if (properties.HasChildren) run.Append(properties.CloneNode(true));
                run.Append(new W.Text(pieces[i]) { Space = SpaceProcessingModeValues.Preserve });
                paragraph.Append(run);
            }
        }
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new W.Styles();

        styles.Append(new W.DocDefaults(
            new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                new W.RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI", EastAsia = "Segoe UI" },
                new W.FontSize { Val = "22" },
                new W.FontSizeComplexScript { Val = "22" })),
            new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle(
                new W.SpacingBetweenLines { After = "160", Line = "276", LineRule = LineSpacingRuleValues.Auto }))));

        styles.Append(new W.Style(
            new W.StyleName { Val = "Normal" },
            new W.StyleParagraphProperties(),
            new W.StyleRunProperties())
        { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });

        var sizes = new Dictionary<int, string> { [1] = "44", [2] = "32", [3] = "26", [4] = "24", [5] = "22", [6] = "22" };
        foreach (var (level, size) in sizes)
        {
            styles.Append(new W.Style(
                new W.StyleName { Val = "heading " + level },
                new W.BasedOn { Val = "Normal" },
                new W.NextParagraphStyle { Val = "Normal" },
                new W.StyleParagraphProperties(
                    new W.KeepNext(),
                    new W.SpacingBetweenLines { Before = level <= 2 ? "240" : "160", After = "120" },
                    new W.OutlineLevel { Val = level - 1 }),
                new W.StyleRunProperties(
                    new W.Bold(),
                    new W.FontSize { Val = size }))
            { Type = StyleValues.Paragraph, StyleId = "Heading" + level });
        }

        styles.Append(new W.Style(
            new W.StyleName { Val = "Code" },
            new W.BasedOn { Val = "Normal" },
            new W.StyleParagraphProperties(
                new W.SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto },
                new W.Shading { Fill = "F2F2F4", Val = ShadingPatternValues.Clear }),
            new W.StyleRunProperties(
                new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" },
                new W.FontSize { Val = "19" }))
        { Type = StyleValues.Paragraph, StyleId = "Code" });

        part.Styles = styles;
        part.Styles.Save();
    }
}
