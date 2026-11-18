using System.IO;
using System.Text.Json.Serialization;

namespace ConvertApp;

// MARK: - Input kinds

public enum MediaKind { Audio, Video, Image, Pdf, Document }

public static class MediaKinds
{
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "mov", "qt", "mkv", "webm", "avi", "divx", "flv", "f4v", "wmv", "asf",
        "ts", "mts", "m2ts", "3gp", "3g2", "ogv", "mxf", "vob", "mpg", "mpeg", "mpe", "m2v",
        "rm", "rmvb", "dv", "y4m",
    };

    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "wav", "wave", "opus", "flac", "m4a", "m4b", "aac", "ogg", "oga", "wma", "aiff",
        "aif", "aifc", "alac", "ac3", "eac3", "dts", "amr", "mp2", "mka", "ape", "wv", "tta",
        "dsf", "dff", "caf", "au", "snd", "mid", "midi",
    };

    private static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jpe", "jfif", "png", "apng", "heic", "heif", "avif", "webp", "tif",
        "tiff", "gif", "bmp", "dib", "ico", "cur", "jxl", "tga", "psd", "dds",
        // Camera raw: ffmpeg reads several of these, WIC reads them with the vendor codec.
        "cr2", "cr3", "nef", "nrw", "arw", "srf", "sr2", "dng", "raf", "orf", "rw2", "pef", "srw",
    };

    private static readonly HashSet<string> Document = new(StringComparer.OrdinalIgnoreCase)
    {
        "docx", "doc", "docm", "odt", "rtf", "html", "htm", "xhtml", "txt", "text", "log",
        "md", "markdown", "mdown", "mkd", "textile", "rst", "csv", "json", "xml", "yaml", "yml",
    };

    public static MediaKind? Of(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return null;
        if (Video.Contains(ext)) return MediaKind.Video;
        if (Audio.Contains(ext)) return MediaKind.Audio;
        if (Image.Contains(ext)) return MediaKind.Image;
        if (ext == "pdf") return MediaKind.Pdf;
        if (Document.Contains(ext)) return MediaKind.Document;
        return null;
    }

    public static string Noun(this MediaKind kind) => kind switch
    {
        MediaKind.Audio => "Audio",
        MediaKind.Video => "Video",
        MediaKind.Image => "Image",
        MediaKind.Pdf => "PDF",
        _ => "Document",
    };

    /// <summary>Key used for the little vector icon in the window header.</summary>
    public static string IconKey(this MediaKind kind) => kind switch
    {
        MediaKind.Audio => "audio",
        MediaKind.Video => "video",
        MediaKind.Image => "image",
        MediaKind.Pdf => "pdf",
        _ => "document",
    };

    public static IReadOnlySet<OutGroup> OutputGroups(this MediaKind kind) => kind switch
    {
        MediaKind.Audio => new HashSet<OutGroup> { OutGroup.Audio },
        MediaKind.Video => new HashSet<OutGroup> { OutGroup.Video, OutGroup.Audio },
        MediaKind.Image => new HashSet<OutGroup> { OutGroup.Image },
        MediaKind.Pdf => new HashSet<OutGroup> { OutGroup.PdfPages },
        _ => new HashSet<OutGroup> { OutGroup.Document },
    };
}

// MARK: - Output targets

public enum OutGroup { Video, Audio, Image, Document, PdfPages }

public static class OutGroupNames
{
    public static string Label(this OutGroup group) => group switch
    {
        OutGroup.Video => "Video",
        OutGroup.Audio => "Audio",
        OutGroup.Image => "Image",
        OutGroup.Document => "Document",
        _ => "From PDF",
    };
}

public sealed record Target(string Id, string Label, string Ext, OutGroup Group)
{
    /// <summary>Group name used to group the format picker.</summary>
    public string GroupLabel => Group.Label();

    /// <summary>The format picker binds to Target objects, so this is the visible text.</summary>
    public override string ToString() => Label;
}

public static class Targets
{
    private static readonly object Gate = new();
    private static List<Target>? all;

    public static IReadOnlyList<Target> All
    {
        get
        {
            lock (Gate) return all ??= Build();
        }
    }

    /// <summary>
    /// Forgets the cached list. Used after ffmpeg has been downloaded, so formats whose
    /// encoders it turned out not to have stop being offered.
    /// </summary>
    public static void Refresh()
    {
        lock (Gate) all = null;
    }

    public static Target? ById(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(t => t.Id == id);

    /// <summary>Output groups valid for every selected file.</summary>
    public static List<(OutGroup Group, List<Target> Targets)> GroupsFor(IReadOnlyCollection<MediaKind> kinds)
    {
        if (kinds.Count == 0) return [];
        var allowed = new HashSet<OutGroup>(Enum.GetValues<OutGroup>());
        foreach (var kind in kinds) allowed.IntersectWith(kind.OutputGroups());
        return Enum.GetValues<OutGroup>()
            .Where(allowed.Contains)
            .Select(g => (g, All.Where(t => t.Group == g).ToList()))
            .Where(pair => pair.Item2.Count > 0)
            .ToList();
    }

    /// <summary>Sensible first choice, skipping the format the files are already in.</summary>
    public static Target? Preferred(IReadOnlyCollection<MediaKind> kinds, IReadOnlySet<string> sourceExts, IReadOnlyList<Target> available)
    {
        MediaKind? only = kinds.Count == 1 ? kinds.First() : null;
        if (kinds.Count == 2 && kinds.Contains(MediaKind.Audio) && kinds.Contains(MediaKind.Video))
            only = MediaKind.Audio;

        string[] order = only switch
        {
            MediaKind.Audio => ["mp3", "wav", "m4a"],
            MediaKind.Video => ["mp4-h264", "mov-prores"],
            MediaKind.Image => ["jpg", "png"],
            MediaKind.Pdf => ["pdf-png"],
            MediaKind.Document => ["pdf-doc", "docx"],
            _ => [],
        };

        foreach (var id in order)
        {
            var t = available.FirstOrDefault(x => x.Id == id);
            if (t is not null && !sourceExts.Contains(t.Ext)) return t;
        }
        return available.FirstOrDefault(t => !sourceExts.Contains(t.Ext)) ?? available.FirstOrDefault();
    }

    private static List<Target> Build()
    {
        var all = new List<Target>();
        void Add(OutGroup group, params (string Id, string Label, string Ext)[] items)
        {
            foreach (var (id, label, ext) in items)
                all.Add(new Target(id, label, ext, group));
        }

        Add(OutGroup.Video,
            ("mp4-h264", "MP4 (H.264)", "mp4"),
            ("mp4-hevc", "MP4 (HEVC / H.265)", "mp4"),
            ("mov-prores", "MOV (ProRes 422)", "mov"),
            ("webm", "WebM (VP9)", "webm"),
            ("mkv", "MKV (H.264)", "mkv"),
            ("mp4-av1", "MP4 (AV1)", "mp4"),
            ("gif", "GIF (animated)", "gif"));

        Add(OutGroup.Audio,
            ("mp3", "MP3", "mp3"),
            ("m4a", "M4A (AAC)", "m4a"),
            ("he-aac", "M4A (HE-AAC, small)", "m4a"),
            ("wav", "WAV", "wav"),
            ("aiff", "AIFF", "aiff"),
            ("flac", "FLAC", "flac"),
            ("alac", "ALAC (Apple Lossless)", "m4a"),
            ("opus", "Opus", "opus"),
            ("ogg", "OGG (Vorbis)", "ogg"),
            ("wma", "WMA", "wma"),
            ("ac3", "AC3 (Dolby Digital)", "ac3"),
            ("wv", "WavPack", "wv"),
            ("mp2", "MP2", "mp2"));

        Add(OutGroup.Image,
            ("jpg", "JPEG", "jpg"),
            ("png", "PNG", "png"),
            ("avif", "AVIF", "avif"),
            ("webp", "WebP", "webp"),
            ("tiff", "TIFF", "tiff"),
            ("gif-img", "GIF", "gif"),
            ("bmp", "BMP", "bmp"),
            ("pdf-img", "PDF", "pdf"));

        Add(OutGroup.Document,
            ("pdf-doc", "PDF", "pdf"),
            ("docx", "Word (DOCX)", "docx"),
            ("doc", "Word 97–2004 (DOC)", "doc"),
            ("odt", "OpenDocument (ODT)", "odt"),
            ("rtf", "Rich Text (RTF)", "rtf"),
            ("txt", "Plain Text", "txt"),
            ("html", "HTML", "html"));

        Add(OutGroup.PdfPages,
            ("pdf-png", "PNG (one per page)", "png"),
            ("pdf-jpg", "JPEG (one per page)", "jpg"),
            ("pdf-docx", "Word (DOCX)", "docx"),
            ("pdf-txt", "Plain Text", "txt"));

        // Hide what this machine can't actually write, exactly like the original hides
        // WebP when cwebp isn't installed. ffmpeg itself is downloaded on demand, so until
        // it arrives every ffmpeg-backed format stays visible.
        bool haveFfmpeg = Tools.HasFfmpeg;
        bool CanEncode(string encoder) => !haveFfmpeg || FfmpegCaps.CanEncode(encoder);
        bool libfdk = CanEncode("libfdk_aac");
        bool libx264 = CanEncode("libx264");
        bool libx265 = CanEncode("libx265");
        bool av1 = CanEncode("libsvtav1") || CanEncode("libaom-av1");
        bool vp9 = CanEncode("libvpx-vp9") && CanEncode("libopus");
        bool prores = CanEncode("prores_ks");
        bool webp = CanEncode("libwebp");
        bool avif = av1;

        all.RemoveAll(t => t.Id switch
        {
            "he-aac" => !libfdk,
            "mp4-h264" or "mkv" => !libx264,
            "mp4-hevc" => !libx265,
            "webm" => !vp9,
            "mov-prores" => !prores,
            "mp4-av1" => !av1,
            "webp" => !webp,
            "avif" => !avif,
            "heic" => true, // Windows has no reliable HEIC encoder; ffmpeg can't write it either.
            "doc" => !Documents.LibreOfficePath_IsAvailable,
            _ => false,
        });

        return all;
    }
}

// MARK: - Per-target options

public static class TargetOptions
{
    public static bool IsLossyAudio(this Target t) =>
        t.Id is "mp3" or "m4a" or "he-aac" or "opus" or "ogg" or "wma" or "ac3" or "mp2";

    public static bool IsLosslessAudio(this Target t) =>
        t.Id is "wav" or "aiff" or "flac" or "alac" or "wv";

    public static bool IsLossyImage(this Target t) =>
        t.Id is "jpg" or "heic" or "avif" or "webp";

    public static bool HasHardwareOption(this Target t) =>
        t.Id is "mp4-h264" or "mp4-hevc" or "mkv";

    public static int[] BitrateOptions(this Target t) => t.Id switch
    {
        "mp3" => [0, 320, 256, 192, 160, 128, 96, 64],
        "m4a" => [320, 256, 192, 160, 128, 96],
        "he-aac" => [96, 80, 64, 48, 32],
        "opus" => [256, 192, 160, 128, 96, 64, 48, 32],
        "ogg" => [320, 256, 192, 160, 128, 96],
        "wma" => [256, 192, 160, 128, 96],
        "ac3" => [640, 448, 384, 256, 192],
        "mp2" => [384, 320, 256, 192, 128],
        _ => [],
    };

    public static int DefaultBitrate(this Target t) => t.Id switch
    {
        "m4a" => 256,
        "he-aac" => 64,
        "opus" => 160,
        "ogg" or "wma" => 192,
        "ac3" => 448,
        "mp2" => 256,
        _ => 0,
    };

    public static int[] SampleRates(this Target t)
    {
        if (t.Id == "opus") return [];
        return t.IsLossyAudio() ? [0, 44100, 48000] : [0, 44100, 48000, 88200, 96000];
    }

    public static bool HasChannelOption(this Target t) => t.Id != "ogg";

    /// <summary>Whether the panel has anything to show under "Advanced" for this format.</summary>
    public static bool HasSettings(this Target t) =>
        !(t.Group == OutGroup.Document && t.Id != "pdf-doc") &&
        t.Id is not ("pdf-txt" or "pdf-docx");
}

public sealed class ConvSettings
{
    // Audio
    public int AudioBitrate { get; set; } = 0;     // kbps; 0 = MP3 VBR best
    public int SampleRate { get; set; } = 0;       // 0 = same as source
    public int Channels { get; set; } = 0;         // 0 = same, 1 = mono, 2 = stereo
    public int BitDepth { get; set; } = 0;         // 0 = auto, 16, 24
    // Video
    public double VideoQuality { get; set; } = 0.65;
    public int VideoHeight { get; set; } = 0;
    public int Fps { get; set; } = 0;
    public bool KeepAudio { get; set; } = true;
    public bool Hardware { get; set; } = true;
    public int GifWidth { get; set; } = 480;
    public int GifFps { get; set; } = 15;
    // Image
    public double ImageQuality { get; set; } = 0.85;
    public int MaxSize { get; set; } = 0;
    // Documents / PDF
    public bool PaperA4 { get; set; } = true;
    public int PdfDpi { get; set; } = 150;

    public ConvSettings Clone() => (ConvSettings)MemberwiseClone();
}
