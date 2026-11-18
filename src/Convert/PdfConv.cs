using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using UglyToad.PdfPig.Content;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;

namespace ConvertApp;

/// <summary>
/// PDF → images uses Windows' own PDF renderer (Windows.Data.Pdf) and text extraction uses
/// PdfPig, since Windows has no built-in text extractor. PDF → Word reuses that extraction
/// and feeds the document model behind the DOCX writer.
/// </summary>
public static class PdfConv
{
    public static async Task<List<string>> ConvertAsync(
        string input, Target target, ConvSettings settings, IProgress<double>? progress, CancellationToken ct)
    {
        if (target.Id == "pdf-txt")
            return [await Task.Run(() => ExtractText(input), ct)];
        if (target.Id == "pdf-docx")
            return [await Task.Run(() => ExtractDocx(input, progress, ct), ct)];
        return await RenderPagesAsync(input, target, settings, progress, ct);
    }

    // MARK: - Text

    private static string ExtractText(string input)
    {
        StringBuilder text = new();
        try
        {
            using var document = PdfPigDocument.Open(input);
            foreach (var page in document.GetPages())
            {
                // page.Text concatenates fragments without spaces, so rebuild lines from words.
                double? baseline = null;
                foreach (var word in page.GetWords())
                {
                    var y = word.BoundingBox.Bottom;
                    if (baseline is { } previous)
                    {
                        if (Math.Abs(y - previous) > 2) text.AppendLine();
                        else text.Append(' ');
                    }
                    text.Append(word.Text);
                    baseline = y;
                }
                text.AppendLine();
            }
        }
        catch (Exception ex)
        {
            throw new ConvertException(ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase)
                ? "This PDF is password-protected"
                : "Couldn't open this PDF");
        }

        // Word-level extraction leaves a space before punctuation; tidy it up.
        var cleaned = Regex.Replace(text.ToString(), @"[ \t]+([,.;:!?])", "$1");
        if (cleaned.Trim().Length == 0)
            throw new ConvertException("This PDF contains no extractable text");

        var output = Fs.UniqueOutput(input, "txt");
        File.WriteAllText(output, cleaned, new UTF8Encoding(true));
        return output;
    }

    // MARK: - Word

    private sealed class PdfLine
    {
        public string Text = "";
        public double Top;
        public double Bottom;
        public double FontSize;
    }

    /// <summary>
    /// Rebuilds headings and paragraphs from the positions of the PDF's words, then writes
    /// them as a real Word document. Images and the exact layout can't be carried over this
    /// way; a scanned PDF has no text to extract and is reported as such.
    /// </summary>
    private static string ExtractDocx(string input, IProgress<double>? progress, CancellationToken ct)
    {
        var model = BuildDocModel(input, progress, ct);
        if (!model.Blocks.Any(block => block is not PageBreakBlock))
            throw new ConvertException("This PDF contains no extractable text — it may be a scan");

        var output = Fs.UniqueOutput(input, "docx");
        try
        {
            model.ToDocx(output);
        }
        catch
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            throw;
        }
        return output;
    }

    private static DocModel BuildDocModel(string input, IProgress<double>? progress, CancellationToken ct)
    {
        var pages = new List<List<PdfLine>>();
        var sizes = new List<double>();

        try
        {
            using var document = PdfPigDocument.Open(input);
            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var lines = ReadLines(page);
                pages.Add(lines);
                foreach (var line in lines) sizes.Add(line.FontSize);
            }
        }
        catch (Exception ex) when (ex is not ConvertException and not OperationCanceledException)
        {
            throw new ConvertException(ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase)
                ? "This PDF is password-protected"
                : "Couldn't open this PDF");
        }

        var bodySize = Median(sizes);
        var model = new DocModel();
        var started = false;

        for (var index = 0; index < pages.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var lines = pages[index];
            if (lines.Count > 0)
            {
                if (started) model.Blocks.Add(new PageBreakBlock());
                AppendPage(lines, bodySize, model.Blocks);
                started = true;
            }
            progress?.Report((index + 1) / (double)pages.Count);
        }

        return model;
    }

    private static List<PdfLine> ReadLines(Page page)
    {
        var words = page.GetWords()
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .OrderByDescending(word => word.BoundingBox.Bottom)
            .ThenBy(word => word.BoundingBox.Left)
            .ToList();
        if (words.Count == 0) return [];

        // PDF coordinates start at the bottom-left. Words sharing a baseline form one line;
        // a little tolerance absorbs superscripts and slightly misaligned glyphs.
        var groups = new List<List<Word>>();
        foreach (var word in words)
        {
            if (groups.Count > 0)
            {
                var reference = groups[^1][0].BoundingBox;
                var tolerance = Math.Max(1.5, Math.Min(reference.Height, word.BoundingBox.Height) * 0.35);
                if (Math.Abs(word.BoundingBox.Bottom - reference.Bottom) <= tolerance)
                {
                    groups[^1].Add(word);
                    continue;
                }
            }
            groups.Add([word]);
        }

        var lines = new List<PdfLine>(groups.Count);
        foreach (var group in groups)
        {
            var ordered = group.OrderBy(word => word.BoundingBox.Left).ToList();
            var text = string.Join(" ", ordered.Select(word => word.Text));
            text = Regex.Replace(text, @"[ \t]+([,.;:!?])", "$1").Trim();
            if (text.Length == 0) continue;

            lines.Add(new PdfLine
            {
                Text = text,
                Top = ordered.Max(word => word.BoundingBox.Top),
                Bottom = ordered.Min(word => word.BoundingBox.Bottom),
                FontSize = ordered.Max(WordFontSize),
            });
        }

        return lines;
    }

    private static double WordFontSize(Word word)
    {
        double size = 0;
        foreach (var letter in word.Letters)
            if (letter.PointSize > size) size = letter.PointSize;
        return size > 0 ? size : word.BoundingBox.Height;
    }

    private static void AppendPage(List<PdfLine> lines, double bodySize, List<DocBlock> blocks)
    {
        var paragraph = new List<PdfLine>();
        var gapLimit = Math.Max(2.5, bodySize * 0.45);

        void Flush()
        {
            if (paragraph.Count == 0) return;
            var text = JoinLines(paragraph);
            var lineCount = paragraph.Count;
            var single = paragraph[0];
            paragraph.Clear();
            if (text.Length == 0) return;

            if (HeadingLevel(lineCount, text, single.FontSize, bodySize) is { } level)
            {
                var heading = new HeadingBlock { Level = level };
                heading.Inlines.Add(new DocInline { Text = text });
                blocks.Add(heading);
            }
            else
            {
                var block = new ParagraphBlock();
                block.Inlines.Add(new DocInline { Text = text });
                blocks.Add(block);
            }
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (paragraph.Count > 0)
            {
                // A gap noticeably larger than normal leading starts a new paragraph.
                if (paragraph[^1].Bottom - line.Top > gapLimit) Flush();
            }
            paragraph.Add(line);
        }
        Flush();
    }

    private static string JoinLines(List<PdfLine> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            if (text.Length == 0)
            {
                text.Append(line.Text);
                continue;
            }

            // Rejoin words hyphenated across a line break: "conver-" + "sion".
            if (text[^1] == '-' && text.Length > 1 && text[^2] != '-' &&
                line.Text.Length > 0 && char.IsLower(line.Text[0]))
            {
                text.Length--;
                text.Append(line.Text);
            }
            else
            {
                text.Append(' ').Append(line.Text);
            }
        }
        return text.ToString();
    }

    private static int? HeadingLevel(int lineCount, string text, double fontSize, double bodySize)
    {
        if (lineCount != 1 || bodySize <= 0 || text.Length > 120) return null;
        return (fontSize / bodySize) switch
        {
            >= 1.8 => 1,
            >= 1.45 => 2,
            >= 1.2 => 3,
            _ => (int?)null,
        };
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    // MARK: - Pages

    private static async Task<List<string>> RenderPagesAsync(
        string input, Target target, ConvSettings settings, IProgress<double>? progress, CancellationToken ct)
    {
        StorageFile file;
        WinPdfDocument document;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input));
            document = await WinPdfDocument.LoadFromFileAsync(file);
        }
        catch
        {
            throw new ConvertException("Couldn't open this PDF — it may be password-protected");
        }

        {
            var count = (int)document.PageCount;
            if (count == 0) throw new ConvertException("This PDF has no pages");

            var sizes = ReadPageSizes(input);

            // One page → a file next to the PDF. Several → a folder with page 1, page 2, …
            string? folder = null;
            if (count > 1)
            {
                folder = Fs.UniqueFolder(input, " pages");
                Directory.CreateDirectory(folder);
            }
            var single = Fs.UniqueOutput(input, target.Ext);

            for (var index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();

                using var page = document.GetPage((uint)index);
                using var stream = new InMemoryRandomAccessStream();

                var scale = Math.Max(1, settings.PdfDpi) / 72.0;
                var options = new PdfPageRenderOptions();
                if (index < sizes.Count)
                {
                    var (widthPt, heightPt) = sizes[index];
                    options.DestinationWidth = (uint)Math.Max(1, Math.Round(widthPt * scale));
                    options.DestinationHeight = (uint)Math.Max(1, Math.Round(heightPt * scale));
                }
                else
                {
                    // No size information: ask for the page at twice its natural size.
                    options.DestinationWidth = (uint)Math.Max(1, page.Size.Width * 2);
                }

                await page.RenderToStreamAsync(stream, options);

                var bytes = new byte[stream.Size];
                using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                {
                    await reader.LoadAsync((uint)stream.Size);
                    reader.ReadBytes(bytes);
                    reader.DetachStream();
                }

                var output = folder is null
                    ? single
                    : Path.Combine(folder, $"page {index + 1}.{target.Ext}");

                WriteImage(bytes, output, target, settings);
                progress?.Report((index + 1) / (double)count);
            }

            return [folder ?? single];
        }
    }

    private static List<(double Width, double Height)> ReadPageSizes(string input)
    {
        var sizes = new List<(double, double)>();
        try
        {
            using var document = PdfPigDocument.Open(input);
            foreach (var page in document.GetPages())
            {
                var width = page.Width;
                var height = page.Height;
                sizes.Add(page.Rotation.SwapsAxis ? (height, width) : (width, height));
            }
        }
        catch
        {
            // The renderer will use its own size when PdfPig can't read the page boxes.
        }
        return sizes;
    }

    private static void WriteImage(byte[] png, string output, Target target, ConvSettings settings)
    {
        using var input = new MemoryStream(png);
        var frame = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        BitmapEncoder encoder = target.Id == "pdf-jpg"
            ? new JpegBitmapEncoder { QualityLevel = Math.Clamp((int)Math.Round(settings.ImageQuality * 100), 1, 100) }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = File.Create(output);
        encoder.Save(stream);
    }
}
