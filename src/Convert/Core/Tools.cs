using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace ConvertApp;

/// <summary>A conversion failure whose message is shown to the user as is.</summary>
public sealed class ConvertException : Exception
{
    public ConvertException(string message) : base(message) { }
}

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Convert");

    public static string BinDir => Path.Combine(DataDir, "bin");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static string ExePath =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.Combine(AppContext.BaseDirectory, "Convert.exe");

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(BinDir);
    }
}

/// <summary>
/// Finds (or fetches) the external tools. Windows has no ffmpeg of its own, so the app
/// looks in its own data folder, on the PATH and in the usual install locations, and can
/// download a static build on first use.
/// </summary>
public static class Tools
{
    private static readonly object Gate = new();
    private static bool _searched;
    private static string? _ffmpeg;
    private static string? _ffprobe;

    public static string? Ffmpeg
    {
        get { Search(); return _ffmpeg; }
    }

    public static string? Ffprobe
    {
        get { Search(); return _ffprobe; }
    }

    public static bool HasFfmpeg => Ffmpeg is not null && Ffprobe is not null;

    private static void Search()
    {
        lock (Gate)
        {
            if (_searched) return;
            _searched = true;

            _ffmpeg = Locate("ffmpeg", "CONVERT_FFMPEG");
            _ffprobe = Locate("ffprobe", "CONVERT_FFPROBE");
        }
    }

    public static void Forget()
    {
        lock (Gate)
        {
            _searched = false;
            _ffmpeg = null;
            _ffprobe = null;
        }
    }

    private static string? Locate(string name, string envVar)
    {
        var explicitPath = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;

        var inBin = Path.Combine(AppPaths.BinDir, name + ".exe");
        if (File.Exists(inBin)) return inBin;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry; ignore it.
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(programFiles, "ffmpeg", "bin", name + ".exe"),
            Path.Combine(localAppData, "Microsoft", "WinGet", "Links", name + ".exe"),
            Path.Combine(@"C:\ffmpeg", "bin", name + ".exe"),
            Path.Combine(localAppData, "Programs", "ffmpeg", "bin", name + ".exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        return null;
    }

    /// <summary>
    /// Downloads a static Windows build of ffmpeg into %LOCALAPPDATA%\Convert\bin.
    /// </summary>
    public static async Task DownloadFfmpegAsync(IProgress<string>? status, CancellationToken ct)
    {
        const string url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        AppPaths.EnsureDirs();

        status?.Report("Downloading ffmpeg…");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var remote = await response.Content.ReadAsStreamAsync(ct);

        using var buffer = total > 0 ? new MemoryStream((int)total) : new MemoryStream();
        var chunk = new byte[81920];
        long read = 0;
        var lastPercent = -1;
        int n;
        while ((n = await remote.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, n);
            read += n;
            if (total > 0)
            {
                var percent = (int)(read * 100 / total);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    status?.Report($"Downloading ffmpeg… {percent}%");
                }
            }
        }

        status?.Report("Installing ffmpeg…");
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);

        foreach (var name in new[] { "ffmpeg", "ffprobe" })
        {
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').EndsWith($"/bin/{name}.exe", StringComparison.OrdinalIgnoreCase));
            if (entry is null) throw new ConvertException($"The downloaded archive didn't contain {name}.exe");

            var destination = Path.Combine(AppPaths.BinDir, name + ".exe");
            var temp = destination + ".part";
            await using (var input = entry.Open())
            await using (var output = File.Create(temp))
                await input.CopyToAsync(output, ct);
            File.Move(temp, destination, overwrite: true);
        }

        Forget();
        if (!HasFfmpeg) throw new ConvertException("ffmpeg was downloaded but couldn't be started");
        status?.Report("ffmpeg ready");
    }

    /// <summary>Opens a URL in the default browser.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Nothing useful to do if the shell can't open it.
        }
    }

    public static bool HasWebView2()
    {
        try
        {
            var key = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                "pv", null) as string;
            if (!string.IsNullOrEmpty(key)) return true;
            key = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                "pv", null) as string;
            return !string.IsNullOrEmpty(key);
        }
        catch
        {
            return true;
        }
    }
}
