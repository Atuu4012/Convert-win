using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConvertApp;

/// <summary>
/// Image conversion on top of WIC, Windows' built-in imaging layer (the counterpart of
/// macOS ImageIO). WebP and AVIF go through ffmpeg, which is already there for video.
/// </summary>
public static class ImageConv
{
    /// <summary>
    /// Inputs Windows can't reliably decode on its own (HEIC/AVIF need a Store codec, the
    /// rest have no WIC decoder at all). ffmpeg is fetched up front for these.
    /// </summary>
    public static bool NeedsFfmpeg(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".heic" or ".heif" or ".avif" or ".jxl" or ".psd" or ".dds" or ".tga" or
        ".cr2" or ".cr3" or ".nef" or ".nrw" or ".arw" or ".srf" or ".sr2" or ".dng" or
        ".raf" or ".orf" or ".rw2" or ".pef" or ".srw";

    public static Task<List<string>> ConvertAsync(string input, Target target, ConvSettings settings, CancellationToken ct) =>
        Task.Run(() => ConvertCore(input, target, settings, ct), ct);

    private static List<string> ConvertCore(string input, Target target, ConvSettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var output = Fs.UniqueOutput(input, target.Ext);
        var (source, orientation) = Load(input);
        var bitmap = Orient(source, orientation);
        bitmap = Resize(bitmap, settings.MaxSize);

        if (target.Id is "jpg" or "bmp" or "pdf-img")
            bitmap = Flatten(bitmap);

        switch (target.Id)
        {
            case "webp":
                WriteThroughFfmpeg(bitmap, output, "webp", settings.ImageQuality, ct);
                return [output];

            case "avif":
                WriteThroughFfmpeg(bitmap, output, "avif", settings.ImageQuality, ct);
                return [output];

            case "pdf-img":
                WriteImagePdf(bitmap, output, settings.ImageQuality);
                return [output];

            default:
                Save(bitmap, output, target.Id, settings.ImageQuality);
                return [output];
        }
    }

    // MARK: - Decode

    private static (BitmapSource Source, int Orientation) Load(string path)
    {
        try
        {
            return LoadWithWic(path);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException
                                       or IOException or System.Runtime.InteropServices.COMException)
        {
            // HEIC, AVIF, some camera raw files: WIC needs a Store codec that may be missing.
            var decoded = DecodeWithFfmpeg(path);
            try
            {
                return LoadWithWic(decoded);
            }
            finally
            {
                TryDelete(decoded);
            }
        }
    }

    private static (BitmapSource, int) LoadWithWic(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new ConvertException("Couldn't read this image");

        var frame = decoder.Frames[0];
        var orientation = ReadOrientation(frame);
        BitmapSource bitmap = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        bitmap.Freeze();
        return (bitmap, orientation);
    }

    private static string DecodeWithFfmpeg(string path)
    {
        var ffmpeg = Tools.Ffmpeg ?? throw new ConvertException(
            "Unsupported image format. Install ffmpeg (it is downloaded automatically when converting audio or video).");
        var temp = Path.Combine(Path.GetTempPath(), "convert-" + Guid.NewGuid().ToString("N") + ".png");
        var result = Shell.Run(ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-i", path, "-frames:v", "1", temp);
        if (!result.Ok || !File.Exists(temp))
            throw new ConvertException(Shell.LastLine(result.StdErr) ?? "Couldn't read this image");
        return temp;
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata metadata) return 1;
        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                if (metadata.GetQuery(query) is { } value)
                {
                    var orientation = Convert.ToInt32(value);
                    if (orientation is >= 1 and <= 8) return orientation;
                }
            }
            catch
            {
                // Not every container has EXIF; try the next query.
            }
        }
        return 1;
    }

    private static BitmapSource Orient(BitmapSource source, int orientation)
    {
        BitmapSource Rotate(double angle) => new TransformedBitmap(source, new RotateTransform(angle));
        BitmapSource FlipX() => new TransformedBitmap(source, new ScaleTransform(-1, 1));

        return orientation switch
        {
            2 => FlipX(),
            3 => Rotate(180),
            4 => new TransformedBitmap(source, new ScaleTransform(1, -1)),
            5 => new TransformedBitmap(FlipX(), new RotateTransform(90)),
            6 => Rotate(90),
            7 => new TransformedBitmap(FlipX(), new RotateTransform(270)),
            8 => Rotate(270),
            _ => source,
        };
    }

    private static BitmapSource Resize(BitmapSource source, int maxSize)
    {
        if (maxSize <= 0) return source;
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= maxSize) return source;
        var scale = (double)maxSize / longest;
        var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        resized.Freeze();
        return resized;
    }

    /// <summary>Composites the image onto white so JPEG/BMP/PDF have no transparent black.</summary>
    private static BitmapSource Flatten(BitmapSource source)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var rgbStride = width * 3;
        var output = new byte[rgbStride * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            var outRow = y * rgbStride;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var a = pixels[i + 3];
                var outIndex = outRow + x * 3;
                if (a == 255)
                {
                    output[outIndex] = pixels[i];
                    output[outIndex + 1] = pixels[i + 1];
                    output[outIndex + 2] = pixels[i + 2];
                }
                else
                {
                    var inverse = 255 - a;
                    output[outIndex] = (byte)((pixels[i] * a + 255 * inverse) / 255);
                    output[outIndex + 1] = (byte)((pixels[i + 1] * a + 255 * inverse) / 255);
                    output[outIndex + 2] = (byte)((pixels[i + 2] * a + 255 * inverse) / 255);
                }
            }
        }

        var flattened = BitmapSource.Create(width, height, source.DpiX, source.DpiY,
            PixelFormats.Bgr24, null, output, rgbStride);
        flattened.Freeze();
        return flattened;
    }

    // MARK: - Encode

    private static void Save(BitmapSource bitmap, string output, string id, double quality)
    {
        BitmapEncoder encoder = id switch
        {
            "jpg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp((int)Math.Round(quality * 100), 1, 100) },
            "png" => new PngBitmapEncoder(),
            "tiff" => new TiffBitmapEncoder(),
            "gif-img" => new GifBitmapEncoder(),
            "bmp" => new BmpBitmapEncoder(),
            _ => throw new ConvertException("This image format isn't available on this PC"),
        };

        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(output);
        encoder.Save(stream);
    }

    private static void WriteThroughFfmpeg(BitmapSource bitmap, string output, string format, double quality, CancellationToken ct)
    {
        var ffmpeg = Tools.Ffmpeg ?? throw new ConvertException($"{format.ToUpperInvariant()} output needs ffmpeg");
        var temp = Path.Combine(Path.GetTempPath(), "convert-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            Save(bitmap, temp, "png", 1);
            var q = Math.Clamp(quality, 0, 1);
            string[] args = format == "webp"
                ? ["-y", "-hide_banner", "-loglevel", "error", "-i", temp,
                   "-c:v", "libwebp", "-quality", ((int)Math.Round(q * 100)).ToString(), "-compression_level", "6", output]
                : ["-y", "-hide_banner", "-loglevel", "error", "-i", temp,
                   "-c:v", FfmpegCaps.CanEncode("libsvtav1") ? "libsvtav1" : "libaom-av1",
                   "-crf", ((int)Math.Round(55 - q * 40)).ToString(),
                   "-pix_fmt", "yuv420p", "-still-picture", "1", output];

            var result = Shell.Run(ffmpeg, args);
            if (!result.Ok)
            {
                TryDelete(output);
                throw new ConvertException(Shell.LastLine(result.StdErr) ?? $"Couldn't write {format.ToUpperInvariant()}");
            }
        }
        finally
        {
            TryDelete(temp);
        }
        ct.ThrowIfCancellationRequested();
    }

    // MARK: - PDF from one image

    private static void WriteImagePdf(BitmapSource bitmap, string output, double quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp((int)Math.Round(quality * 100), 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var imageStream = new MemoryStream();
        encoder.Save(imageStream);
        var jpeg = imageStream.ToArray();

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        // PDF measures in points (1/72 inch), images in pixels; keep the physical size.
        var dpiX = bitmap.DpiX is > 1 and < 2000 ? bitmap.DpiX : 96;
        var dpiY = bitmap.DpiY is > 1 and < 2000 ? bitmap.DpiY : 96;
        var pageWidth = width * 72.0 / dpiX;
        var pageHeight = height * 72.0 / dpiY;
        var content = Encoding.ASCII.GetBytes(
            $"q\n{pageWidth.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} 0 0 " +
            $"{pageHeight.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} 0 0 cm\n/Im0 Do\nQ\n");

        using var file = new FileStream(output, FileMode.Create, FileAccess.Write);
        void Write(string text)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            file.Write(bytes, 0, bytes.Length);
        }

        var offsets = new long[6];
        Write("%PDF-1.4\n");

        offsets[1] = file.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = file.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = file.Position;
        Write($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth:0.###} {pageHeight:0.###}] " +
              "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");

        offsets[4] = file.Position;
        Write($"4 0 obj\n<< /Length {content.Length} >>\nstream\n");
        file.Write(content, 0, content.Length);
        Write("\nendstream\nendobj\n");

        offsets[5] = file.Position;
        Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} " +
              "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpeg.Length + " >>\nstream\n");
        file.Write(jpeg, 0, jpeg.Length);
        Write("\nendstream\nendobj\n");

        var xref = file.Position;
        Write("xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
