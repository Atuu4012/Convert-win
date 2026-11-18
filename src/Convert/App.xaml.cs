using System.IO;
using System.Text.Json;
using System.Windows;

namespace ConvertApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureDirs();

        var args = e.Args;
        var first = args.FirstOrDefault()?.ToLowerInvariant();

        switch (first)
        {
            case "--install-menu":
                QuickAction.Install();
                Shutdown(0);
                return;
            case "--uninstall-menu":
                QuickAction.Uninstall();
                Shutdown(0);
                return;
            case "--cli":
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                RunCli([.. args.Skip(1)]);
                return;
            case "--help" or "-h" or "/?":
                ConsoleBridge.Attach();
                Console.WriteLine("Convert — like CloudConvert, but local.");
                Console.WriteLine();
                Console.WriteLine("usage: Convert.exe [file…]                       opens the Convert window");
                Console.WriteLine("       Convert.exe --cli <target> <file…>       converts with default settings");
                Console.WriteLine("       Convert.exe --install-menu               adds the Explorer right-click entry");
                Console.WriteLine("       Convert.exe --uninstall-menu             removes it");
                Console.WriteLine();
                Console.WriteLine("targets: " + string.Join(" ", Targets.All.Select(t => t.Id)));
                Shutdown(0);
                return;
        }

        var files = args.Where(a => !a.StartsWith('-')).ToArray();
        var window = new MainWindow(files);
        MainWindow = window;
        window.Show();
    }

    /// <summary>Test mode: `Convert.exe --cli &lt;target-id&gt; file…` converts with default settings.</summary>
    private async void RunCli(string[] args)
    {
        ConsoleBridge.Attach();
        var exitCode = 2;

        if (args.Length >= 2 && Targets.ById(args[0]) is { } target)
        {
            var settings = new ConvSettings();
            var json = Environment.GetEnvironmentVariable("CONVERT_SETTINGS");
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    settings = JsonSerializer.Deserialize<ConvSettings>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? settings;
                }
                catch (JsonException)
                {
                    Console.WriteLine("CONVERT_SETTINGS isn't valid JSON");
                }
            }

            exitCode = 0;

            var needsFfmpeg = target.Group is OutGroup.Audio or OutGroup.Video ||
                              target.Id is "webp" or "avif" ||
                              (target.Group == OutGroup.Image && args.Skip(1).Any(ImageConv.NeedsFfmpeg));
            if (needsFfmpeg && !Tools.HasFfmpeg)
            {
                try
                {
                    var progress = new Progress<string>(line => Console.WriteLine(line));
                    await Tools.DownloadFfmpegAsync(progress, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("FAIL ffmpeg: " + ex.Message);
                    Shutdown(1);
                    return;
                }
            }

            foreach (var path in args.Skip(1))
            {
                var full = Path.GetFullPath(path);
                var started = DateTime.Now;
                try
                {
                    var outputs = await Converter.ConvertAsync(full, target, settings, null, CancellationToken.None);
                    var secs = (DateTime.Now - started).TotalSeconds.ToString("0.00");
                    var names = string.Join(", ", outputs.Select(Path.GetFileName));
                    Console.WriteLine($"OK   {Path.GetFileName(full)} → {names} ({secs}s)");
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    Console.WriteLine($"FAIL {Path.GetFileName(full)}: {ex.Message}");
                }
            }
        }
        else
        {
            Console.WriteLine("usage: Convert.exe --cli <target> file…");
            Console.WriteLine("targets: " + string.Join(" ", Targets.All.Select(t => t.Id)));
        }

        Shutdown(exitCode);
    }
}

/// <summary>Gives a WinExe a console when it is started from cmd.exe or PowerShell.</summary>
internal static class ConsoleBridge
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private const int AttachParentProcess = -1;

    private static bool _attached;

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;
        try
        {
            AttachConsole(AttachParentProcess);
            var stdout = Console.OpenStandardOutput();
            var writer = new StreamWriter(stdout) { AutoFlush = true };
            Console.SetOut(writer);
            var stderr = Console.OpenStandardError();
            Console.SetError(new StreamWriter(stderr) { AutoFlush = true });
        }
        catch
        {
            // No parent console (started from Explorer); output is simply discarded.
        }
    }
}
