using System.IO;
using Microsoft.Win32;

namespace ConvertApp;

/// <summary>
/// The Explorer right-click entry ("Convert with Convert…"). It lives under HKCU, so no
/// administrator rights are needed, and it hands every selected file to the app.
/// </summary>
public static class QuickAction
{
    private const string VerbKey = @"Software\Classes\*\shell\Convert";
    private const string CommandKey = VerbKey + @"\command";

    public static bool IsInstalled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(CommandKey);
            return key?.GetValue(null) is string command && command.Contains("Convert", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Install()
    {
        var exe = AppPaths.ExePath;

        using (var verb = Registry.CurrentUser.CreateSubKey(VerbKey, writable: true))
        {
            verb.SetValue(null, "Convert with Convert…");
            verb.SetValue("Icon", $"\"{exe}\",0");
            // "Player" tells Explorer to pass every selected file on the command line.
            verb.SetValue("MultiSelectModel", "Player");
            verb.SetValue("Position", "Bottom");
        }

        using (var command = Registry.CurrentUser.CreateSubKey(CommandKey, writable: true))
            command.SetValue(null, $"\"{exe}\" \"%1\"");

        NotifyShell();
    }

    public static void Uninstall()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(VerbKey, throwOnMissingSubKey: false); }
        catch { /* The key may be in use by Explorer; it will go on the next refresh. */ }
        NotifyShell();
    }

    /// <summary>Tells Explorer that file associations changed, so menus refresh without a reboot.</summary>
    public static void NotifyShell()
    {
        try { Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, Native.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch { }
    }

    private static class Native
    {
        public const uint SHCNE_ASSOCCHANGED = 0x08000000;
        public const uint SHCNF_IDLIST = 0x0000;

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        public static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}
