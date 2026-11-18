using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;

namespace ConvertApp;

/// <summary>
/// PDF → images uses Windows' own PDF renderer (Windows.Data.Pdf) and text extraction uses
/// PdfPig, since Windows has no built-in text extractor.
/// </summary>
public static class PdfConv
{
    public static async Task<List<string>> ConvertAsync(
        string input, Target target, ConvSettings settings, IProgress<double>? progress, CancellationToken ct)
    {
        return target.Id == "pdf-txt"
            ? [await Task.Run(() => ExtractText(input), ct)]
            : await RenderPagesAsync(input, target, settings, progress, ct);
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
