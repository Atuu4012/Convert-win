using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ConvertApp;

/// <summary>Reads an HTML document into the block model (used for HTML and Markdown input).</summary>
public static class HtmlDoc
{
    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "main", "aside", "figure", "figcaption",
        "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "table", "pre", "blockquote", "hr", "dl", "form",
    };

    public static DocModel Parse(string html)
    {
        var document = new HtmlDocument { OptionFixNestedTags = true };
        document.LoadHtml(html);

        var model = new DocModel();
        var body = document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode;
        ParseBlocks(body, model.Blocks);
        return model;
    }

    // MARK: - Blocks

    private static void ParseBlocks(HtmlNode parent, List<DocBlock> blocks)
    {
        foreach (var node in parent.ChildNodes)
        {
            if (node.NodeType == HtmlNodeType.Text)
            {
                var text = Clean(node.InnerText);
                if (text.Trim().Length > 0) blocks.Add(ParagraphWith(text));
                continue;
            }
            if (node.NodeType != HtmlNodeType.Element) continue;

            switch (node.Name.ToLowerInvariant())
            {
                case "script":
                case "style":
                case "head":
                case "title":
                case "meta":
                case "link":
                case "noscript":
                    break;

                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                    var heading = new HeadingBlock { Level = node.Name[1] - '0' };
                    ParseInlines(node, heading.Inlines, new Style());
                    blocks.Add(heading);
                    break;

                case "p":
                case "dd":
                case "dt":
                case "figcaption":
                    blocks.Add(ParagraphFrom(node));
                    break;

                case "div":
                case "section":
                case "article":
                case "header":
                case "footer":
                case "main":
                case "aside":
                case "figure":
                case "form":
                case "body":
                case "html":
                    if (HasBlockChild(node)) ParseBlocks(node, blocks);
                    else if (Clean(node.InnerText).Trim().Length > 0) blocks.Add(ParagraphFrom(node));
                    break;

                case "ul":
                case "ol":
                    blocks.Add(ParseList(node, 1));
                    break;

                case "table":
                    var table = ParseTable(node);
                    if (table is not null) blocks.Add(table);
                    break;

                case "pre":
                    blocks.Add(new CodeBlock { Text = HtmlEntity.DeEntitize(node.InnerText).Trim('\n', '\r') });
                    break;

                case "blockquote":
                    var quote = new QuoteBlock();
                    ParseBlocks(node, quote.Blocks);
                    if (quote.Blocks.Count > 0) blocks.Add(quote);
                    break;

                case "hr":
                    blocks.Add(new RuleBlock());
                    break;

                case "br":
                    break;

                default:
                    if (HasBlockChild(node)) ParseBlocks(node, blocks);
                    else if (Clean(node.InnerText).Trim().Length > 0) blocks.Add(ParagraphFrom(node));
                    break;
            }
        }
    }

    private static ParagraphBlock ParagraphWith(string text)
    {
        var paragraph = new ParagraphBlock();
        paragraph.Inlines.Add(new DocInline { Text = text });
        return paragraph;
    }

    private static ParagraphBlock ParagraphFrom(HtmlNode node)
    {
        var paragraph = new ParagraphBlock();
        ParseInlines(node, paragraph.Inlines, new Style());
        DocModel.TrimEdges(paragraph.Inlines);
        return paragraph;
    }

    private static bool HasBlockChild(HtmlNode node) =>
        node.ChildNodes.Any(n => n.NodeType == HtmlNodeType.Element && BlockElements.Contains(n.Name));

    private static ListBlock ParseList(HtmlNode list, int level)
    {
        var block = new ListBlock { Ordered = list.Name.Equals("ol", StringComparison.OrdinalIgnoreCase) };
        foreach (var item in list.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element))
        {
            if (!item.Name.Equals("li", StringComparison.OrdinalIgnoreCase)) continue;

            var entry = new ListEntry { Level = level };
            foreach (var child in item.ChildNodes)
            {
                if (child.NodeType == HtmlNodeType.Text)
                {
                    Append(entry.Inlines, Clean(child.InnerText), new Style());
                    continue;
                }
                if (child.NodeType != HtmlNodeType.Element) continue;
                if (child.Name.Equals("ul", StringComparison.OrdinalIgnoreCase) ||
                    child.Name.Equals("ol", StringComparison.OrdinalIgnoreCase))
                    continue;
                ParseInlines(child, entry.Inlines, new Style());
            }
            DocModel.TrimEdges(entry.Inlines);
            if (entry.Inlines.Count > 0) block.Items.Add(entry);

            foreach (var nested in item.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element &&
                         (n.Name.Equals("ul", StringComparison.OrdinalIgnoreCase) ||
                          n.Name.Equals("ol", StringComparison.OrdinalIgnoreCase))))
            {
                var sub = ParseList(nested, level + 1);
                foreach (var subItem in sub.Items)
                {
                    subItem.Ordered = sub.Ordered;
                    block.Items.Add(subItem);
                }
            }
        }
        return block;
    }

    private static TableBlock? ParseTable(HtmlNode table)
    {
        var result = new TableBlock();
        foreach (var row in table.DescendantsAndSelf())
        {
            if (!row.Name.Equals("tr", StringComparison.OrdinalIgnoreCase)) continue;
            // Skip rows that belong to a nested table.
            if (row.Ancestors().Any(a => a.Name.Equals("table", StringComparison.OrdinalIgnoreCase) && a != table))
                continue;

            var tableRow = new TableRow();
            foreach (var cell in row.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element))
            {
                if (!cell.Name.Equals("td", StringComparison.OrdinalIgnoreCase) &&
                    !cell.Name.Equals("th", StringComparison.OrdinalIgnoreCase)) continue;

                if (cell.Name.Equals("th", StringComparison.OrdinalIgnoreCase)) tableRow.IsHeader = true;
                var tableCell = new TableCell();
                if (HasBlockChild(cell)) ParseBlocks(cell, tableCell.Blocks);
                else
                {
                    var paragraph = new ParagraphBlock();
                    ParseInlines(cell, paragraph.Inlines, new Style());
                    DocModel.TrimEdges(paragraph.Inlines);
                    if (paragraph.Inlines.Count > 0) tableCell.Blocks.Add(paragraph);
                }
                tableRow.Cells.Add(tableCell);
            }
            if (tableRow.Cells.Count > 0) result.Rows.Add(tableRow);
        }
        return result.Rows.Count > 0 ? result : null;
    }

    // MARK: - Inlines

    private sealed class Style
    {
        public bool Bold, Italic, Underline, Strike, Code;
        public string? Link;

        public Style Copy() => (Style)MemberwiseClone();
    }

    private static void ParseInlines(HtmlNode node, List<DocInline> output, Style style)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                Append(output, Clean(child.InnerText), style);
                continue;
            }
            if (child.NodeType != HtmlNodeType.Element) continue;

            switch (child.Name.ToLowerInvariant())
            {
                case "br":
                    Append(output, "\n", style);
                    break;
                case "b":
                case "strong":
                    ParseInlines(child, output, Styled(style, s => s.Bold = true));
                    break;
                case "i":
                case "em":
                    ParseInlines(child, output, Styled(style, s => s.Italic = true));
                    break;
                case "u":
                case "ins":
                    ParseInlines(child, output, Styled(style, s => s.Underline = true));
                    break;
                case "s":
                case "strike":
                case "del":
                    ParseInlines(child, output, Styled(style, s => s.Strike = true));
                    break;
                case "code":
                case "kbd":
                case "samp":
                case "tt":
                    ParseInlines(child, output, Styled(style, s => s.Code = true));
                    break;
                case "a":
                    var href = child.GetAttributeValue("href", "");
                    var linked = Styled(style, s => { if (href.Length > 0) s.Link = href; });
                    ParseInlines(child, output, linked);
                    break;
                case "img":
                    Append(output, child.GetAttributeValue("alt", ""), style);
                    break;
                case "script":
                case "style":
                    break;
                default:
                    ParseInlines(child, output, style);
                    break;
            }
        }
    }

    private static Style Styled(Style style, Action<Style> change)
    {
        var copy = style.Copy();
        change(copy);
        return copy;
    }

    private static void Append(List<DocInline> output, string text, Style style)
    {
        if (text.Length == 0) return;
        if (output.Count > 0 && SameStyle(output[^1], style) && output[^1].Link == style.Link &&
            !text.StartsWith('\n'))
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

    private static bool SameStyle(DocInline a, Style s) =>
        a.Bold == s.Bold && a.Italic == s.Italic && a.Underline == s.Underline &&
        a.Strike == s.Strike && a.Code == s.Code;

    private static string Clean(string text)
    {
        var replaced = HtmlEntity.DeEntitize(text).Replace('\u00A0', ' ').Replace("\u200B", "");
        return Regex.Replace(replaced, @"[ \t\r\n\f]+", " ");
    }
}
