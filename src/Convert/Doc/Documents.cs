using System.IO;

namespace ConvertApp;

/// <summary>
/// Document conversion. Windows has no textutil equivalent, so the pipeline reads every
/// supported document into a small block model and writes it back out. LibreOffice, when
/// installed, is used for the legacy binary .doc format it alone understands.
/// </summary>
public static partial class Documents
{
    private static readonly Lazy<string?> LibreOffice = new(FindLibreOffice);

    public static string? LibreOfficePath => LibreOffice.Value;

    public static bool LibreOfficePath_IsAvailable => LibreOfficePath is not null;

    private static string? FindLibreOffice()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[]
        {
            Path.Combine(programFiles, "LibreOffice", "program", "soffice.exe"),
            Path.Combine(programFilesX86, "LibreOffice", "program", "soffice.exe"),
            Path.Combine(programFiles, "LibreOffice 7", "program", "soffice.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    public static bool IsMarkdown(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".mdown" or ".mkd";

    /// <summary>Plain-text formats that are displayed as prose, not parsed.</summary>
    public static bool IsPlainText(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".text" or ".log" or ".csv" or ".json" or ".xml"
            or ".yaml" or ".yml" or ".rst" or ".textile" or ".ini" or ".cfg" or ".conf";

    public static bool IsRtf(string path) => Path.GetExtension(path).Equals(".rtf", StringComparison.OrdinalIgnoreCase);
    public static bool IsHtml(string path) => Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm" or ".xhtml";

    /// <summary>Converts a document to a target format and returns the files (or folder) written.</summary>
    public static async Task<List<string>> ConvertAsync(
        string input, Target target, ConvSettings settings, IProgress<double>? progress, CancellationToken ct)
    {
        if (target.Id == "pdf-doc")
            return [await ToPdfAsync(input, settings, ct)];

        // Legacy Word files: hand over to LibreOffice, the only free offline reader/writer.
        if (Path.GetExtension(input).Equals(".doc", StringComparison.OrdinalIgnoreCase))
        {
            if (LibreOfficePath is null)
                throw new ConvertException("Word 97–2004 files need LibreOffice, which isn't installed.");
            return [await LibreOfficeConvertAsync(input, target.Ext, input, ct)];
        }

        if (target.Id == "doc" )
        {
            if (LibreOfficePath is null)
                throw new ConvertException("Writing Word 97–2004 files needs LibreOffice, which isn't installed.");
            var html = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".html");
            try
            {
                File.WriteAllText(html, DocModel.Load(input).ToHtml(Path.GetFileNameWithoutExtension(input)));
                return [await LibreOfficeConvertAsync(html, "doc", input, ct)];
            }
            finally { TryDelete(html); }
        }

        var model = DocModel.Load(input);
        var output = Fs.UniqueOutput(input, target.Ext);

        switch (target.Ext)
        {
            case "html":
                if (IsHtml(input) && target.Id == "html")
                {
                    File.Copy(input, output, overwrite: false);
                    return [output];
                }
                File.WriteAllText(output, model.ToHtml(Path.GetFileNameWithoutExtension(input)));
                return [output];

            case "txt":
                File.WriteAllText(output, model.ToPlainText());
                return [output];

            case "rtf":
                File.WriteAllText(output, model.ToRtf(), System.Text.Encoding.ASCII);
                return [output];

            case "docx":
                model.ToDocx(output);
                return [output];

            case "odt":
                model.ToOdt(output);
                return [output];
        }

        throw new ConvertException($"Can't convert to {target.Label}");
    }

    private static async Task<string> ToPdfAsync(string input, ConvSettings settings, CancellationToken ct)
    {
        var output = Fs.UniqueOutput(input, "pdf");
        var model = DocModel.Load(input);
        var html = model.ToHtml(Path.GetFileNameWithoutExtension(input), forPrint: true, a4: settings.PaperA4);
        try
        {
            await HtmlPrint.ToPdfAsync(html, output, settings.PaperA4, ct);
        }
        catch
        {
            TryDelete(output);
            throw;
        }
        return output;
    }

    private static async Task<string> LibreOfficeConvertAsync(
        string input, string ext, string namedAfter, CancellationToken ct)
    {
        var soffice = LibreOfficePath ?? throw new ConvertException("LibreOffice isn't installed.");
        var outDir = Path.Combine(Path.GetTempPath(), "convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var filter = ext switch
            {
                "pdf" => "pdf",
                "docx" => "docx:MS Word 2007 XML",
                "doc" => "doc:MS Word 97",
                "odt" => "odt",
                "rtf" => "rtf",
                "html" => "html",
                _ => ext,
            };
            await Shell.RunWithProgressAsync(soffice,
                ["--headless", "--norestore", "--convert-to", filter, "--outdir", outDir, input],
                0, null, ct);

            var produced = Directory.GetFiles(outDir).FirstOrDefault()
                ?? throw new ConvertException("LibreOffice didn't produce a file");
            var dest = Fs.UniqueOutput(namedAfter, ext);
            File.Move(produced, dest);
            return dest;
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
