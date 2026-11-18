using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace ConvertApp;

public enum Phase { Idle, Running, Done }

/// <summary>State behind the Convert window (the original's ConvertModel).</summary>
public sealed class ConvertModel : INotifyPropertyChanged
{
    public ObservableCollection<string> Files { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];
    public List<string> Outputs { get; } = [];

    private readonly SettingsStore store;
    private Phase phase = Phase.Idle;
    private Target? selectedTarget;
    private ConvSettings settings = new();
    private double progress;
    private string currentName = "";
    private int skipped;
    private bool stopped;
    private int trashed;
    private bool trashOriginals;
    private bool showAdvanced;
    private bool downloading;
    private string status = "";
    private CancellationTokenSource? cts;

    public ConvertModel()
    {
        store = SettingsStore.Load();
        trashOriginals = store.TrashOriginals;
        showAdvanced = store.ShowAdvanced;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? StateChanged;
    public event Action? FilesChanged;

    // MARK: Files

    public void AddFiles(IEnumerable<string> paths)
    {
        if (Phase == Phase.Running) return;
        if (Phase == Phase.Done) Reset();

        var added = false;
        foreach (var raw in paths)
        {
            string path;
            try { path = Path.GetFullPath(raw); }
            catch { continue; }
            if (!File.Exists(path)) continue;

            if (Files.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase))) continue;
            if (MediaKinds.Of(path) is null) { Skipped++; continue; }

            Files.Add(path);
            added = true;
        }

        if (added || Phase == Phase.Done) ChooseTarget();
        FilesChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void RemoveFile(string path)
    {
        if (Phase != Phase.Idle) return;
        Files.Remove(path);
        ChooseTarget();
        FilesChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public int Skipped
    {
        get => skipped;
        private set { if (skipped != value) { skipped = value; Raise(); } }
    }

    // MARK: Format choice

    public List<Target> AvailableTargets { get; private set; } = [];
    public List<TargetGroupInfo> Groups { get; private set; } = [];
    public bool HasUnsupportedMix => Files.Count > 0 && AvailableTargets.Count == 0;

    public Target? SelectedTarget
    {
        get => selectedTarget;
        set
        {
            if (selectedTarget == value) return;
            selectedTarget = value;
            if (value is not null)
            {
                store.LastTargets[GroupKey] = value.Id;
                LoadSettings(value);
            }
            Raise();
            Raise(nameof(TargetTitle));
            Raise(nameof(HasSettings));
            StateChanged?.Invoke();
        }
    }

    public ConvSettings Settings => settings;

    public bool HasSettings => selectedTarget?.HasSettings() == true;

    public bool ShowAdvanced
    {
        get => showAdvanced;
        set
        {
            if (showAdvanced == value) return;
            showAdvanced = value;
            store.ShowAdvanced = value;
            store.Save();
            Raise();
            StateChanged?.Invoke();
        }
    }

    public string TargetTitle => selectedTarget?.Label ?? "";

    public IReadOnlySet<MediaKind> Kinds => Files.Select(MediaKinds.Of).Where(k => k is not null).Select(k => k!.Value).ToHashSet();

    public MediaKind? PrimaryKind => Files.Count > 0 && Kinds.Count == 1 ? Kinds.First() : null;

    private string GroupKey => string.Join("+", Kinds.Select(k => k.ToString()).OrderBy(s => s, StringComparer.Ordinal));

    private void ChooseTarget()
    {
        var kinds = Kinds;
        Groups = Targets.GroupsFor(kinds)
            .Select(g => new TargetGroupInfo(g.Group, g.Targets))
            .ToList();
        AvailableTargets = Groups.SelectMany(g => g.Targets).ToList();

        if (AvailableTargets.Count == 0)
        {
            selectedTarget = null;
            Raise(nameof(SelectedTarget));
            Raise(nameof(AvailableTargets));
            return;
        }

        if (selectedTarget is not null && AvailableTargets.Contains(selectedTarget)) return;

        store.LastTargets.TryGetValue(GroupKey, out var lastId);
        var exts = Files.Select(f => Path.GetExtension(f).TrimStart('.').ToLowerInvariant()).ToHashSet();
        var chosen = AvailableTargets.FirstOrDefault(t => t.Id == lastId)
                     ?? Targets.Preferred(kinds, exts, AvailableTargets)
                     ?? AvailableTargets[0];

        // Assign the field directly: loading settings for the new format happens here.
        selectedTarget = chosen;
        store.LastTargets[GroupKey] = chosen.Id;
        LoadSettings(chosen);
        Raise(nameof(SelectedTarget));
        Raise(nameof(AvailableTargets));
        Raise(nameof(TargetTitle));
        Raise(nameof(HasSettings));
    }

    private void LoadSettings(Target target)
    {
        var s = store.SettingsFor(target.Id);
        var bitrates = target.BitrateOptions();
        if (bitrates.Length > 0 && !bitrates.Contains(s.AudioBitrate)) s.AudioBitrate = target.DefaultBitrate();
        if (!target.SampleRates().Contains(s.SampleRate)) s.SampleRate = 0;
        settings = s;
        Raise(nameof(Settings));
    }

    /// <summary>Called by the settings panel after any edit.</summary>
    public void SaveSettings()
    {
        if (selectedTarget is null) return;
        store.SetSettings(selectedTarget.Id, settings);
        store.Save();
    }

    // MARK: Header text

    public string Title => Files.Count switch
    {
        0 => "Convert",
        1 => Path.GetFileName(Files[0]),
        _ => $"{Files.Count} files",
    };

    public string Subtitle
    {
        get
        {
            if (Files.Count == 0) return "Drop files here, or right-click them in Explorer → Convert…";
            var parts = new List<string>();
            if (Files.Count == 1)
            {
                var f = Files[0];
                if (MediaKinds.Of(f) is { } kind) parts.Add(kind.Noun());
                parts.Add(Fs.FormatSize(Fs.Size(f)));
            }
            else
            {
                var names = string.Join(", ", Files.Take(3).Select(Path.GetFileName));
                parts.Add(Files.Count > 3 ? $"{names} + {Files.Count - 3} more" : names);
            }
            if (Skipped > 0) parts.Add($"{Skipped} unsupported skipped");
            return string.Join(" · ", parts);
        }
    }

    public string KindIconKey => PrimaryKind?.IconKey() ?? "app";

    // MARK: Run state

    public Phase Phase
    {
        get => phase;
        private set { if (phase != value) { phase = value; Raise(); StateChanged?.Invoke(); } }
    }

    public double Progress
    {
        get => progress;
        private set { if (Math.Abs(progress - value) > 0.0001) { progress = value; Raise(); } }
    }

    public string CurrentName
    {
        get => currentName;
        private set { if (currentName != value) { currentName = value; Raise(); } }
    }

    public string Status
    {
        get => status;
        private set { if (status != value) { status = value; Raise(); } }
    }

    public bool IsDownloadingFfmpeg
    {
        get => downloading;
        private set { if (downloading != value) { downloading = value; Raise(); } }
    }

    public bool Stopped
    {
        get => stopped;
        private set { if (stopped != value) { stopped = value; Raise(); } }
    }

    public int Trashed
    {
        get => trashed;
        private set { if (trashed != value) { trashed = value; Raise(); } }
    }

    public bool TrashOriginals
    {
        get => trashOriginals;
        set
        {
            if (trashOriginals == value) return;
            trashOriginals = value;
            store.TrashOriginals = value;
            store.Save();
            Raise();
        }
    }

    public bool HasFfmpegWarning =>
        phase == Phase.Idle && selectedTarget is { } t && t.Group is OutGroup.Audio or OutGroup.Video && !Tools.HasFfmpeg;

    public bool CanConvert => phase == Phase.Idle && selectedTarget is not null && Files.Count > 0;

    private void Reset()
    {
        phase = Phase.Idle;
        Outputs.Clear();
        Errors.Clear();
        Progress = 0;
        Stopped = false;
        Trashed = 0;
        Skipped = 0;
        Status = "";
        CurrentName = "";
        Raise(nameof(Phase));
        Raise(nameof(CanConvert));
        StateChanged?.Invoke();
    }

    public void Cancel()
    {
        if (phase != Phase.Running) return;
        Stopped = true;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task StartAsync()
    {
        if (selectedTarget is null || phase != Phase.Idle || Files.Count == 0) return;

        if (Errors.Count > 0 || Outputs.Count > 0)
        {
            Outputs.Clear();
            Errors.Clear();
            Trashed = 0;
        }
        Progress = 0;
        Stopped = false;
        Status = "";
        Phase = Phase.Running;
        cts?.Dispose();
        cts = new CancellationTokenSource();
        var ct = cts.Token;

        var target = selectedTarget;
        var settingsSnapshot = settings.Clone();
        var list = Files.ToList();
        var trash = trashOriginals;
        var n = (double)list.Count;

        try
        {
            // Audio, video and the ffmpeg-backed image formats need ffmpeg. Fetch it on demand.
            var needsFfmpeg = target.Group is OutGroup.Audio or OutGroup.Video ||
                              target.Id is "webp" or "avif" ||
                              (target.Group == OutGroup.Image && list.Any(ImageConv.NeedsFfmpeg));
            if (needsFfmpeg && !Tools.HasFfmpeg)
            {
                IsDownloadingFfmpeg = true;
                Status = "Downloading ffmpeg…";
                try
                {
                    var report = new Progress<string>(s => Status = s);
                    await Tools.DownloadFfmpegAsync(report, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Errors.Add("ffmpeg: " + ex.Message);
                    Phase = Phase.Done;
                    return;
                }
                finally
                {
                    IsDownloadingFfmpeg = false;
                    Status = "";
                }

                // The download may lack an encoder (the essentials build has no libfdk_aac),
                // so rebuild the format list and fall back to a format that still works.
                Targets.Refresh();
                ChooseTarget();
                if (selectedTarget is null)
                {
                    Errors.Add("No format is available for these files");
                    Phase = Phase.Done;
                    return;
                }
                target = selectedTarget;
                settingsSnapshot = settings.Clone();
            }

            for (var i = 0; i < list.Count; i++)
            {
                if (Stopped) break;
                var file = list[i];
                CurrentName = Path.GetFileName(file);
                Progress = i / n;

                var slot = i;
                var report = new Progress<double>(p => Progress = (slot + Math.Clamp(p, 0, 1)) / n);

                try
                {
                    var made = await Converter.ConvertAsync(file, target, settingsSnapshot, report, ct);
                    Outputs.AddRange(made);

                    // Only after a finished, separate output exists on disk; the Recycle Bin keeps it recoverable.
                    if (trash && !Stopped && made.Count > 0 && !made.Contains(file) && made.All(File.Exists))
                    {
                        if (Fs.Recycle(file)) Trashed++;
                        else Errors.Add($"{Path.GetFileName(file)}: converted, but couldn't move it to the Recycle Bin");
                    }
                }
                catch (OperationCanceledException)
                {
                    Stopped = true;
                }
                catch (Exception ex)
                {
                    if (!Stopped) Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        finally
        {
            Progress = 1;
            Phase = Phase.Done;
            cts?.Dispose();
            cts = null;
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record TargetGroupInfo(OutGroup Group, List<Target> Targets)
{
    public string Label => Group.Label();
}
