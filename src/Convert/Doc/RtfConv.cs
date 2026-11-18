using System.Globalization;
using System.Text;

namespace ConvertApp;

/// <summary>
/// A compact RTF reader/writer. It understands the formatting people actually use — bold,
/// italic, underline, strike, paragraphs, line breaks and Unicode — and ignores the rest.
/// </summary>
public static class RtfReader
{
    private sealed class State
    {
        public bool Bold, Italic, Underline, Strike, Skip, GroupStart;
        public State Copy() => (State)MemberwiseClone();
    }

    private static readonly HashSet<string> Destinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "header", "footer", "headerl",
        "headerr", "headerf", "footerl", "footerr", "footerf", "listtable", "listoverridetable",
        "rsidtbl", "generator", "xmlnstbl", "themedata", "colorschememapping", "datastore",
        "latentstyles", "fldinst", "filetbl", "revtbl", "pgptbl", "pnseclvl", "panose",
    };

    public static DocModel Read(string rtf)
    {
        var model = new DocModel();
        var state = new State();
        var stack = new Stack<State>();
        var inlines = new List<DocInline>();
        var buffer = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (buffer.Length == 0) return;
            inlines.Add(new DocInline
            {
                Text = buffer.ToString(),
                Bold = state.Bold,
                Italic = state.Italic,
                Underline = state.Underline,
                Strike = state.Strike,
            });
            buffer.Clear();
        }

        void EndParagraph()
        {
            Flush();
            DocModel.TrimEdges(inlines);
            if (inlines.Count > 0)
            {
                var paragraph = new ParagraphBlock();
                paragraph.Inlines.AddRange(inlines);
                model.Blocks.Add(paragraph);
            }
            inlines.Clear();
        }

        while (i < rtf.Length)
        {
            var c = rtf[i];

            if (c == '{')
            {
                stack.Push(state.Copy());
                state.GroupStart = true;
                i++;
                continue;
            }

            if (c == '}')
            {
                Flush();
                if (stack.Count > 0) state = stack.Pop();
                state.GroupStart = false;
                i++;
                continue;
            }

            if (c is '\r' or '\n') { i++; continue; }

            if (c != '\\')
            {
                if (!state.Skip) buffer.Append(c);
                state.GroupStart = false;
                i++;
                continue;
            }

            // Control sequence
            i++;
            if (i >= rtf.Length) break;
            var next = rtf[i];

            if (next is '\\' or '{' or '}')
            {
                if (!state.Skip) buffer.Append(next);
                state.GroupStart = false;
                i++;
                continue;
            }

            if (next == '\'')
            {
                if (i + 2 < rtf.Length &&
                    byte.TryParse(rtf.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    if (!state.Skip) buffer.Append(DocModel.WesternEuropean.GetString([b]));
                    i += 3;
                }
                else i++;
                state.GroupStart = false;
                continue;
            }

            if (next == '*')
            {
                Flush();
                state.Skip = true;
                i++;
                continue;
            }

            if (!char.IsLetter(next))
            {
                Flush();
                if (!state.Skip)
                {
                    switch (next)
                    {
                        case '~': buffer.Append('\u00A0'); break;
                        case '_': buffer.Append('\u2011'); break;
                        case '-': break;
                        case '\n':
                        case '\r': break;
                        default: break;
                    }
                }
                state.GroupStart = false;
                i++;
                continue;
            }

            // Control word
            var start = i;
            while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
            var word = rtf[start..i];

            var negative = false;
            var hasNumber = false;
            var number = 0;
            if (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i])))
            {
                hasNumber = true;
                if (rtf[i] == '-') { negative = true; i++; }
                var numStart = i;
                while (i < rtf.Length && char.IsDigit(rtf[i])) i++;
                if (int.TryParse(rtf.AsSpan(numStart, i - numStart), out var parsed)) number = negative ? -parsed : parsed;
            }
            if (i < rtf.Length && rtf[i] == ' ') i++; // the space is the word's delimiter

            if (state.GroupStart && Destinations.Contains(word))
            {
                state.Skip = true;
                Flush();
                state.GroupStart = false;
                continue;
            }
            state.GroupStart = false;
            if (state.Skip) continue;

            switch (word)
            {
                case "b": Flush(); state.Bold = !hasNumber || number != 0; break;
                case "i": Flush(); state.Italic = !hasNumber || number != 0; break;
                case "ul": Flush(); state.Underline = true; break;
                case "ulnone": Flush(); state.Underline = false; break;
                case "strike": Flush(); state.Strike = !hasNumber || number != 0; break;
                case "plain": Flush(); state.Bold = state.Italic = state.Underline = state.Strike = false; break;
                case "par": EndParagraph(); break;
                case "line": buffer.Append('\n'); break;
                case "tab": buffer.Append('\t'); break;
                case "u":
                    if (hasNumber && number >= 0) buffer.Append((char)number);
                    else if (hasNumber) buffer.Append((char)(number + 65536));
                    // The optional fallback character after \uN must be skipped.
                    if (i < rtf.Length && rtf[i] != '\\' && rtf[i] != '{' && rtf[i] != '}') i++;
                    break;
                case "emdash": buffer.Append('\u2014'); break;
                case "endash": buffer.Append('\u2013'); break;
                case "lquote": buffer.Append('\u2018'); break;
                case "rquote": buffer.Append('\u2019'); break;
                case "ldblquote": buffer.Append('\u201C'); break;
                case "rdblquote": buffer.Append('\u201D'); break;
                case "bullet": buffer.Append('\u2022'); break;
                case "cell": buffer.Append('\t'); break;
                case "row": EndParagraph(); break;
                default: break;
            }
        }

        EndParagraph();
        return model;
    }
}

public static class RtfWriter
{
    public static string Write(DocModel model)
    {
        var sb = new StringBuilder();
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Segoe UI;}{\f1\fmodern Consolas;}}");
        sb.Append(@"\viewkind4\uc1\pard\f0\fs22 ");
        AppendBlocks(model.Blocks, sb);
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendBlocks(IEnumerable<DocBlock> blocks, StringBuilder sb)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    sb.Append(@"\pard\sa160\sb240 ");
                    sb.Append(@"\b\fs").Append(heading.Level switch
                    {
                        1 => 44,
                        2 => 32,
                        3 => 26,
                        _ => 22,
                    }).Append(' ');
                    AppendInlines(heading.Inlines, sb);
                    sb.Append(@"\b0\fs22\par" + "\n");
                    break;

                case ParagraphBlock paragraph:
                    sb.Append(@"\pard\sa160 ");
                    AppendInlines(paragraph.Inlines, sb);
                    sb.Append(@"\par" + "\n");
                    break;

                case CodeBlock code:
                    sb.Append(@"\pard\sa160\f1 ");
                    foreach (var line in code.Text.Replace("\r\n", "\n").Split('\n'))
                    {
                        AppendEscaped(line, sb);
                        sb.Append(@"\line ");
                    }
                    sb.Append(@"\f0\par" + "\n");
                    break;

                case RuleBlock:
                    sb.Append(@"\pard\sa160 ");
                    for (var i = 0; i < 40; i++) sb.Append(@"\u8212?");
                    sb.Append(@"\par" + "\n");
                    break;

                case QuoteBlock quote:
                    sb.Append(@"\pard\li360\sa160 ");
                    var inner = new StringBuilder();
                    AppendBlocks(quote.Blocks, inner);
                    sb.Append(inner.ToString().Replace(@"\pard", @"\pard\li360"));
                    break;

                case ListBlock list:
                    var n = 1;
                    foreach (var item in list.Items)
                    {
                        var ordered = item.Ordered ?? list.Ordered;
                        sb.Append(@"\pard\li360\fi-240\sa60 ");
                        if (ordered) sb.Append(n++).Append(". ");
                        else sb.Append(@"\u8226? ");
                        AppendInlines(item.Inlines, sb);
                        sb.Append(@"\par" + "\n");
                    }
                    break;

                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        sb.Append(@"\pard\sa60 ");
                        for (var c = 0; c < row.Cells.Count; c++)
                        {
                            if (c > 0) sb.Append(@"\tab ");
                            var cell = new StringBuilder();
                            AppendBlocks(row.Cells[c].Blocks, cell);
                            var text = cell.ToString()
                                .Replace(@"\par" + "\n", " ")
                                .Replace(@"\pard\sa160 ", "");
                            sb.Append(text);
                        }
                        sb.Append(@"\par" + "\n");
                    }
                    break;
            }
        }
    }

    private static void AppendInlines(IEnumerable<DocInline> inlines, StringBuilder sb)
    {
        foreach (var inline in inlines)
        {
            if (inline.Code) sb.Append(@"\f1 ");
            if (inline.Bold) sb.Append(@"\b ");
            if (inline.Italic) sb.Append(@"\i ");
            if (inline.Underline) sb.Append(@"\ul ");
            if (inline.Strike) sb.Append(@"\strike ");

            var lines = inline.Text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.Append(@"\line ");
                AppendEscaped(lines[i], sb);
            }

            if (inline.Strike) sb.Append(@"\strike0 ");
            if (inline.Underline) sb.Append(@"\ulnone ");
            if (inline.Italic) sb.Append(@"\i0 ");
            if (inline.Bold) sb.Append(@"\b0 ");
            if (inline.Code) sb.Append(@"\f0 ");
        }
    }

    private static void AppendEscaped(string text, StringBuilder sb)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case '\t': sb.Append(@"\tab "); break;
                default:
                    if (c < 128) sb.Append(c);
                    else sb.Append(@"\u").Append((int)c).Append('?');
                    break;
            }
        }
    }
}
