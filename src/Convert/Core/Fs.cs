using System.IO;
using System.Runtime.InteropServices;

namespace ConvertApp;

public static class Fs
{
    /// <summary>Next free file name next to the input: "name.ext", then "name 2.ext", …</summary>
    public static string UniqueOutput(string input, string ext, string suffix = "")
    {
        var dir = Path.GetDirectoryName(input) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(input) + suffix;
        var candidate = Path.Combine(dir, baseName + "." + ext);
        var n = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate))
            candidate = Path.Combine(dir, $"{baseName} {n++}.{ext}");
        return candidate;
    }

    public static string UniqueFolder(string input, string suffix)
    {
        var dir = Path.GetDirectoryName(input) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(input) + suffix;
        var candidate = Path.Combine(dir, baseName);
        var n = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate))
            candidate = Path.Combine(dir, $"{baseName} {n++}");
        return candidate;
    }

    public static long Size(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : value.ToString(value >= 10 ? "0" : "0.#") + " " + units[unit];
    }

    /// <summary>
    /// Moves a file to the Recycle Bin. Returns false if the shell refused (network share,
    /// removable media…), in which case the original is left alone.
    /// </summary>
    public static bool Recycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + '\0' + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>Reveals the files in Explorer (selects a single result, opens the folder otherwise).</summary>
    public static void Reveal(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        try
        {
            var first = paths[0];
            var folder = Path.GetDirectoryName(first);
            var target = paths.Count == 1 || folder is null ? first : folder;
            var args = paths.Count == 1 && folder is not null
                ? $"/select,\"{first}\""
                : $"\"{target}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = args,
                UseShellExecute = false,
            });
        }
        catch
        {
            // Explorer is optional.
        }
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
