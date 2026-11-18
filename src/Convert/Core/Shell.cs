using System.Diagnostics;
using System.IO;
using System.Text;

namespace ConvertApp;

public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Small wrappers around System.Diagnostics.Process.</summary>
public static class Shell
{
    private static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Runs a tool to completion and returns its output. Throws with the last stderr line on failure.</summary>
    public static ProcessResult Run(string exe, params string[] args)
    {
        var psi = StartInfo(exe, args);
        try
        {
            using var p = Process.Start(psi) ?? throw new ConvertException("Couldn't start " + Path.GetFileName(exe));
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return new ProcessResult(p.ExitCode, stdout, stderr);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ConvertException($"Couldn't start {Path.GetFileName(exe)}");
        }
    }

    /// <summary>Runs a tool and throws a <see cref="ConvertException"/> if it fails.</summary>
    public static string RunChecked(string exe, params string[] args)
    {
        var r = Run(exe, args);
        if (!r.Ok) throw new ConvertException(LastLine(r.StdErr) ?? $"{Path.GetFileName(exe)} failed");
        return r.StdOut;
    }

    /// <summary>
    /// Runs a long conversion, reporting progress parsed from ffmpeg's <c>-progress pipe:1</c>
    /// output and killing the process when <paramref name="ct"/> is cancelled.
    /// </summary>
    public static async Task RunWithProgressAsync(
        string exe,
        IEnumerable<string> args,
        double durationSeconds,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var psi = StartInfo(exe, args);
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        var stderrDone = new TaskCompletionSource();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (durationSeconds <= 0 || progress is null) return;
            var line = e.Data;
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                var raw = line["out_time_us=".Length..];
                if (long.TryParse(raw, out var us) && us >= 0)
                    progress.Report(Math.Min(1, us / 1_000_000.0 / durationSeconds));
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { stderrDone.TrySetResult(); return; }
            lock (stderr) stderr.AppendLine(e.Data);
        };

        try
        {
            if (!process.Start()) throw new ConvertException($"Couldn't start {Path.GetFileName(exe)}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ConvertException($"Couldn't start {Path.GetFileName(exe)}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        using var registration = ct.Register(() => TryKill(process));
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAny(stderrDone.Task, Task.Delay(500));

        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (process.ExitCode != 0)
        {
            string last;
            lock (stderr) last = LastLine(stderr.ToString()) ?? "";
            throw new ConvertException(last.Length > 0 ? last : $"{Path.GetFileName(exe)} exited with code {process.ExitCode}");
        }
    }

    private static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone.
        }
    }

    public static string? LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        return lines.Count > 0 ? lines[^1] : null;
    }
}
