using System.IO;

namespace ConvertApp;

/// <summary>Dispatches one file to the engine that matches its target.</summary>
public static class Converter
{
    public static async Task<List<string>> ConvertAsync(
        string input, Target target, ConvSettings settings, IProgress<double>? progress, CancellationToken ct)
    {
        if (!File.Exists(input)) throw new ConvertException("File not found");

        switch (target.Group)
        {
            case OutGroup.Audio:
            case OutGroup.Video:
                return [await ConvertAvAsync(input, target, settings, progress, ct)];
            case OutGroup.Image:
                return await ImageConv.ConvertAsync(input, target, settings, ct);
            case OutGroup.Document:
                // Parsing and writing documents is CPU work; keep the window responsive.
                return await Task.Run(() => Documents.ConvertAsync(input, target, settings, progress, ct), ct);
            case OutGroup.PdfPages:
                return await PdfConv.ConvertAsync(input, target, settings, progress, ct);
            default:
                throw new ConvertException("Can't convert to " + target.Label);
        }
    }

    private static async Task<string> ConvertAvAsync(
        string input, Target target, ConvSettings settings, IProgress<double>? progress, CancellationToken ct)
    {
        var ffmpeg = Tools.Ffmpeg;
        if (ffmpeg is null)
        {
            // Called directly (CLI) rather than through the window, which downloads it up front.
            throw new ConvertException(Tools.Ffprobe is null
                ? "ffmpeg isn't installed. Run Convert.exe once from the window, or install it with: winget install ffmpeg"
                : "ffmpeg isn't installed. Install it with: winget install ffmpeg");
        }

        var probe = await Task.Run(() => Ffmpeg.ProbeFile(input), ct);
        if (probe.Ok && target.Group == OutGroup.Audio && !probe.HasAudio)
            throw new ConvertException("This file has no audio track");
        if (probe.Ok && target.Group == OutGroup.Video && !probe.HasVideo)
            throw new ConvertException("This file has no video track");

        var output = Fs.UniqueOutput(input, target.Ext);
        var hardwareCodec = target.Id switch
        {
            "mp4-h264" or "mkv" => "h264",
            "mp4-hevc" => "hevc",
            _ => null,
        };

        var job = new List<string>(Ffmpeg.Common);
        job.AddRange(["-i", input]);
        job.AddRange(target.Group == OutGroup.Audio
            ? Ffmpeg.AudioArgs(target, settings, probe)
            : Ffmpeg.VideoArgs(target, settings, probe, null));
        job.Add(output);

        // Hardware first when asked for, with a transparent software retry if the GPU
        // driver refuses (ffmpeg lists NVENC/QSV/AMF even on machines without them).
        if (hardwareCodec is not null && settings.Hardware)
        {
            var encoder = await Task.Run(() => FfmpegCaps.HardwareEncoder(hardwareCodec), ct);
            if (encoder is not null)
            {
                job.Clear();
                job.AddRange(Ffmpeg.Common);
                job.AddRange(["-i", input]);
                job.AddRange(Ffmpeg.VideoArgs(target, settings, probe, encoder));
                job.Add(output);
                try
                {
                    await Shell.RunWithProgressAsync(ffmpeg, job, probe.Duration, progress, ct);
                    return output;
                }
                catch (OperationCanceledException)
                {
                    TryDelete(output);
                    throw;
                }
                catch (ConvertException)
                {
                    // The GPU encoder didn't work; remember that and fall back to software.
                    FfmpegCaps.ForgetHardware(hardwareCodec);
                    TryDelete(output);
                    job.Clear();
                    job.AddRange(Ffmpeg.Common);
                    job.AddRange(["-i", input]);
                    job.AddRange(Ffmpeg.VideoArgs(target, settings, probe, null));
                    job.Add(output);
                }
            }
        }

        try
        {
            await Shell.RunWithProgressAsync(ffmpeg, job, probe.Duration, progress, ct);
        }
        catch
        {
            TryDelete(output);
            throw;
        }
        return output;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* The file is still being written by a dying child process. */ }
    }
}
