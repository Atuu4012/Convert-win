using System.IO;
using System.Text.Json;

namespace ConvertApp;

/// <summary>
/// Preferences and per-format settings, kept in %LOCALAPPDATA%\Convert\settings.json
/// (the equivalent of the original's UserDefaults).
/// </summary>
public sealed class SettingsStore
{
    public bool TrashOriginals { get; set; }
    public bool ShowAdvanced { get; set; }
    public Dictionary<string, ConvSettings> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> LastTargets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static SettingsStore Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var store = JsonSerializer.Deserialize<SettingsStore>(json, JsonOptions);
                if (store is not null)
                {
                    store.Targets = new Dictionary<string, ConvSettings>(
                        store.Targets ?? [], StringComparer.OrdinalIgnoreCase);
                    store.LastTargets = new Dictionary<string, string>(
                        store.LastTargets ?? [], StringComparer.OrdinalIgnoreCase);
                    return store;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt settings file shouldn't stop the app; start fresh.
        }
        return new SettingsStore();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureDirs();
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a preference is not worth an error dialog.
        }
    }

    public ConvSettings SettingsFor(string targetId) =>
        Targets.TryGetValue(targetId, out var s) ? s.Clone() : new ConvSettings();

    public void SetSettings(string targetId, ConvSettings settings) => Targets[targetId] = settings.Clone();
}
