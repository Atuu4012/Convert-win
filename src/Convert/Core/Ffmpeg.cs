using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConvertApp;

/// <summary>What ffmpeg on this machine can actually encode (and what hardware it has).</summary>
public static class FfmpegCaps
{
    private static readonly Lazy<HashSet<string>> Encoders = new(LoadEncoders);
    private static readonly Dictionary<string, string?> Hardware = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static bool HasFfmpeg => Tools.HasFfmpeg;

    public static bool CanEncode(string name) => Tools.HasFfmpeg && Encoders.Value.Contains(name);

    private static HashSet<string> LoadEncoders()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Tools.Ffmpeg is null) return set;
        try
        {
            var result = Shell.Run(Tools.Ffmpeg, "-hide_banner", "-encoders");
            foreach (var line in result.StdOut.Split('\n'))
            {
                var m = Regex.Match(line, @"^\s*[VAS][A-Z.]{5}\s+(\S+)");
                if (m.Success) set.Add(m.Groups[1].Value);
            }
        }
        catch
        {
            // Treat an unusable ffmpeg as one without encoders.
        }
        return set;
    }

    /// <summary>
    /// A hardware H.264/HEVC encoder that really works on this machine (the encoder list
    /// includes NVENC/QSV/AMF even when there is no matching GPU). Probed once per codec.
    /// </summary>
    public static string? HardwareEncoder(string codec)
    {
        lock (Gate)
        {
            if (Hardware.TryGetValue(codec, out var cached)) return cached;
            var found = ProbeHardware(codec);
            Hardware[codec] = found;
            return found;
        }
    }

    public static bool HasHardware(string codec) => HardwareEncoder(codec) is not null;

    public static void ForgetHardware(string codec)
    {
        lock (Gate) Hardware[codec] = null;
    }

    private static string? ProbeHardware(string codec)
    {
        if (!Tools.HasFfmpeg || Tools.Ffmpeg is null) return null;
        string[] candidates = codec switch
        {
            "h264" => ["h264_nvenc", "h264_qsv", "h264_amf"],
            "hevc" => ["hevc_nvenc", "hevc_qsv", "hevc_amf"],
            _ => [],
        };
        foreach (var encoder in candidates)
        {
            if (!CanEncode(encoder)) continue;
            var result = Shell.Run(Tools.Ffmpeg,
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.1",
                "-frames:v", "2", "-c:v", encoder, "-f", "null", "-");
            if (result.Ok) return encoder;
        }
        return null;
    }
}

public sealed class Probe
{
    public double Duration { get; set; }
    public int SampleRate { get; set; }
    public bool Hires { get; set; }
    public bool HasAudio { get; set; }
    public bool HasVideo { get; set; }
    public bool Ok { get; set; }
}

public static class Ffmpeg
{
    public static readonly string[] Common =
        ["-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:1", "-nostats"];

    // MARK: Probe

    public static Probe ProbeFile(string path)
    {
        var probe = new Probe();
        if (Tools.Ffprobe is null) return probe;

        var result = Shell.Run(Tools.Ffprobe,
            "-v", "error",
            "-show_entries", "format=duration:stream=codec_type,sample_rate,sample_fmt,bits_per_raw_sample,disposition",
            "-of", "json", path);
        if (!result.Ok) return probe;

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;
            probe.Ok = true;

            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var duration) &&
                double.TryParse(Text(duration), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                probe.Duration = d;

            if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
                return probe;

            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var ct) ? Text(ct) : "";
                if (type == "video")
                {
                    var attached = 0;
                    if (stream.TryGetProperty("disposition", out var disp) &&
                        disp.TryGetProperty("attached_pic", out var ap))
                        attached = ap.ValueKind == JsonValueKind.Number ? ap.GetInt32() : 0;
                    if (attached == 0) probe.HasVideo = true;
                }
                else if (type == "audio" && !probe.HasAudio)
                {
                    probe.HasAudio = true;
                    probe.SampleRate = stream.TryGetProperty("sample_rate", out var sr) && int.TryParse(Text(sr), out var rate) ? rate : 0;
                    var fmt = stream.TryGetProperty("sample_fmt", out var sf) ? Text(sf) : "";
                    var bits = stream.TryGetProperty("bits_per_raw_sample", out var b) && int.TryParse(Text(b), out var n) ? n : 0;
                    probe.Hires = fmt.StartsWith("s32", StringComparison.Ordinal) || bits > 16;
                }
            }
        }
        catch (JsonException)
        {
            // ffprobe printed something unexpected; report "no probe".
        }
        return probe;
    }

    private static string Text(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        _ => "",
    };

    // MARK: Audio

    public static string[] AudioArgs(Target t, ConvSettings s, Probe p)
    {
        var a = new List<string> { "-map", "0:a:0", "-vn", "-map_metadata", "0" };
        var br = t.BitrateOptions().Contains(s.AudioBitrate) ? s.AudioBitrate : t.DefaultBitrate();
        var deep = s.BitDepth == 24 || (s.BitDepth == 0 && p.Hires);
        var cap48 = t.IsLossyAudio() && t.Id is not ("opus" or "mp3");

        switch (t.Id)
        {
            case "mp3":
                a.AddRange(br == 0 ? ["-c:a", "libmp3lame", "-q:a", "0"] : ["-c:a", "libmp3lame", "-b:a", $"{br}k"]);
                cap48 = true;
                break;
            case "m4a":
                a.AddRange(["-c:a", "aac", "-b:a", $"{br}k"]);
                break;
            case "he-aac":
                a.AddRange(["-c:a", "libfdk_aac", "-profile:a", "aac_he", "-b:a", $"{br}k"]);
                break;
            case "opus":
                a.AddRange(["-c:a", "libopus", "-b:a", $"{br}k"]);
                break;
            case "ogg":
                a.AddRange(["-c:a", "libvorbis", "-ac", "2", "-q:a", VorbisQuality(br)]);
                break;
            case "wma":
                a.AddRange(["-c:a", "wmav2", "-b:a", $"{br}k"]);
                break;
            case "ac3":
                a.AddRange(["-c:a", "ac3", "-b:a", $"{br}k"]);
                break;
            case "mp2":
                a.AddRange(["-c:a", "mp2", "-b:a", $"{br}k"]);
                break;
            case "wav":
                a.AddRange(["-c:a", deep ? "pcm_s24le" : "pcm_s16le"]);
                break;
            case "aiff":
                a.AddRange(["-c:a", deep ? "pcm_s24be" : "pcm_s16be"]);
                break;
            case "flac":
                a.AddRange(["-c:a", "flac", "-sample_fmt", deep ? "s32" : "s16"]);
                break;
            case "alac":
                a.AddRange(["-c:a", "alac", "-sample_fmt", deep ? "s32p" : "s16p"]);
                break;
            case "wv":
                a.AddRange(["-c:a", "wavpack", "-sample_fmt", deep ? "s32p" : "s16p"]);
                break;
        }

        if (s.SampleRate > 0 && t.SampleRates().Contains(s.SampleRate))
            a.AddRange(["-ar", s.SampleRate.ToString(CultureInfo.InvariantCulture)]);
        else if (cap48 && p.SampleRate > 48000)
            a.AddRange(["-ar", "48000"]);

        if (s.Channels > 0 && t.HasChannelOption())
            a.AddRange(["-ac", s.Channels.ToString(CultureInfo.InvariantCulture)]);

        return [.. a];
    }

    private static string VorbisQuality(int kbps) => kbps switch
    {
        >= 320 => "9",
        >= 256 => "8",
        >= 192 => "6",
        >= 160 => "5",
        >= 128 => "4",
        _ => "2",
    };

    // MARK: Video

    /// <param name="hwEncoder">A working hardware encoder for the codec, or null for software.</param>
    public static string[] VideoArgs(Target t, ConvSettings s, Probe p, string? hwEncoder)
    {
        var q = Math.Clamp(s.VideoQuality, 0, 1);
        var crf = (int)Math.Round(30 - q * 12);        // 18 … 30
        var filters = new List<string>();
        var v = new List<string> { "-map", "0:v:0", "-map_metadata", "0" };

        if (t.Id == "gif")
        {
            var vf = $"fps={s.GifFps},scale=min(iw\\,{s.GifWidth}):-2:flags=lanczos,split[a][b];" +
                     "[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a";
            return ["-map", "0:v:0", "-vf", vf, "-loop", "0", "-an"];
        }

        if (s.Fps > 0) filters.Add($"fps={s.Fps}");
        if (s.VideoHeight > 0) filters.Add($"scale=-2:min(ih\\,{s.VideoHeight})");

        var audio = new List<string> { "-c:a", "aac", "-b:a", "192k" };
        var hardware = hwEncoder is not null;

        switch (t.Id)
        {
            case "mp4-h264":
            case "mkv":
                v.AddRange(hardware ? HardwareArgs(hwEncoder!, "h264", crf) : SoftwareH264(crf));
                v.AddRange(["-pix_fmt", "yuv420p"]);
                break;

            case "mp4-hevc":
                v.AddRange(hardware ? HardwareArgs(hwEncoder!, "hevc", crf) : SoftwareH265(crf));
                v.AddRange(["-tag:v", "hvc1"]);
                break;

            case "mov-prores":
                v.AddRange(["-c:v", "prores_ks", "-profile:v", "2"]);
                audio = ["-c:a", "pcm_s16le"];
                break;

            case "webm":
                v.AddRange(["-c:v", "libvpx-vp9", "-crf", ((int)Math.Round(42 - q * 17)).ToString(CultureInfo.InvariantCulture),
                            "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4"]);
                audio = ["-c:a", "libopus", "-b:a", "128k"];
                break;

            case "mp4-av1":
                if (FfmpegCaps.CanEncode("libsvtav1"))
                    v.AddRange(["-c:v", "libsvtav1", "-crf", ((int)Math.Round(46 - q * 20)).ToString(CultureInfo.InvariantCulture), "-preset", "8"]);
                else
                    v.AddRange(["-c:v", "libaom-av1", "-crf", ((int)Math.Round(50 - q * 20)).ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-cpu-used", "6"]);
                break;
        }

        if (filters.Count > 0) v.AddRange(["-vf", string.Join(",", filters)]);

        if (s.KeepAudio && p.HasAudio)
        {
            v.AddRange(["-map", "0:a:0"]);
            v.AddRange(audio);
        }
        else
        {
            v.Add("-an");
        }

        if (t.Ext is "mp4" or "mov" or "m4a" or "m4v") v.AddRange(["-movflags", "+faststart"]);
        return [.. v];
    }

    private static string[] SoftwareH264(int crf) =>
        ["-c:v", "libx264", "-preset", "medium", "-crf", crf.ToString(CultureInfo.InvariantCulture)];

    private static string[] SoftwareH265(int crf) =>
        ["-c:v", "libx265", "-preset", "medium", "-crf", crf.ToString(CultureInfo.InvariantCulture),
         "-x265-params", "log-level=error"];

    private static string[] HardwareArgs(string encoder, string codec, int crf)
    {
        var q = crf.ToString(CultureInfo.InvariantCulture);
        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
            return ["-c:v", encoder, "-preset", "p5", "-rc", "vbr", "-cq", q, "-b:v", "0"];
        if (encoder.EndsWith("_qsv", StringComparison.Ordinal))
            return ["-c:v", encoder, "-global_quality", q];
        // AMF
        return ["-c:v", encoder, "-quality", "balanced", "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-qp_b", q];
    }
}
