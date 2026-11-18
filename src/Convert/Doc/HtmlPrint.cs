using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ConvertApp;

/// <summary>
/// Turns HTML into a PDF with the Edge WebView2 engine — the closest thing Windows has to
/// the macOS text system the original app prints with. The WebView2 runtime ships with
/// Windows 11 and with Edge on Windows 10.
/// </summary>
public static class HtmlPrint
{
    public static Task ToPdfAsync(string html, string output, bool a4, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(() => ToPdfCoreAsync(html, output, a4, ct)).Task.Unwrap();
        return ToPdfCoreAsync(html, output, a4, ct);
    }

    private static async Task ToPdfCoreAsync(string html, string output, bool a4, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "convert-" + Guid.NewGuid().ToString("N") + ".html");
        await File.WriteAllTextAsync(temp, html, new UTF8Encoding(true), ct);

        var window = new Window
        {
            Width = 900,
            Height = 1200,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Opacity = 0.01,
        };

        WebView2? web = null;
        try
        {
            AppPaths.EnsureDirs();
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(AppPaths.DataDir, "WebView2"));
            web = new WebView2();
            window.Content = web;
            window.Show();

            await web.EnsureCoreWebView2Async(environment);
            var core = web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;

            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, _) => loaded.TrySetResult();
            core.Navigate(new Uri(temp).AbsoluteUri);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

            // Give web fonts and images a moment to settle before printing.
            await Task.Delay(250, ct);

            var settings = core.Environment.CreatePrintSettings();
            settings.PageWidth = a4 ? 8.27 : 8.5;     // inches
            settings.PageHeight = a4 ? 11.69 : 11.0;
            settings.MarginTop = 0;
            settings.MarginBottom = 0;
            settings.MarginLeft = 0;
            settings.MarginRight = 0;
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;

            var ok = await core.PrintToPdfAsync(output, settings);
            if (!ok) throw new ConvertException("Couldn't print this document to PDF");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw new ConvertException(
                "PDF output needs the Microsoft Edge WebView2 runtime. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703");
        }
        finally
        {
            try { web?.Dispose(); } catch { }
            try { window.Close(); } catch { }
            try { File.Delete(temp); } catch { }
        }
    }
}
