using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace ConvertApp;

public partial class MainWindow : Window
{
    private readonly ConvertModel model = new();
    private List<Target>? builtForTargets;
    private Target? builtForTarget;
    private bool syncingPicker;

    public MainWindow(IEnumerable<string> files)
    {
        InitializeComponent();
        DataContext = model;

        model.StateChanged += Refresh;
        model.FilesChanged += Refresh;
        model.PropertyChanged += (_, _) => Refresh();

        model.AddFiles(files);
        Refresh();
    }

    // MARK: Refresh

    private void Refresh()
    {
        if (!ReferenceEquals(builtForTargets, model.AvailableTargets))
        {
            builtForTargets = model.AvailableTargets;
            RebuildPicker();
        }

        var empty = model.Files.Count == 0;
        var mixed = !empty && model.AvailableTargets.Count == 0;

        DropZone.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        MixWarning.Visibility = mixed ? Visibility.Visible : Visibility.Collapsed;
        FormatSection.Visibility = !empty && !mixed ? Visibility.Visible : Visibility.Collapsed;

        IdlePanel.Visibility = model.Phase == Phase.Idle ? Visibility.Visible : Visibility.Collapsed;
        RunningPanel.Visibility = model.Phase == Phase.Running ? Visibility.Visible : Visibility.Collapsed;
        DonePanel.Visibility = model.Phase == Phase.Done ? Visibility.Visible : Visibility.Collapsed;

        TrashCheck.Visibility = model.HasFfmpegWarning ? Visibility.Collapsed : Visibility.Visible;
        FfmpegWarning.Visibility = model.HasFfmpegWarning ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.IsEnabled = model.CanConvert;

        FormatPicker.IsEnabled = model.Phase == Phase.Idle;
        AdvancedToggle.IsEnabled = model.Phase == Phase.Idle;
        SettingsPanel.IsEnabled = model.Phase == Phase.Idle;
        TrashCheck.Content = model.Files.Count > 1
            ? "Move originals to the Recycle Bin"
            : "Move original to the Recycle Bin";

        if (model.IsDownloadingFfmpeg) RunningText.Text = model.Status;
        else if (model.CurrentName.Length > 0) RunningText.Text = $"Converting {model.CurrentName}…";
        else RunningText.Text = "Preparing…";

        builtForTargets = model.AvailableTargets;
        RebuildSettings();
        ApplyHeaderIcon();

        var outputs = model.Outputs.Count;
        if (outputs > 0)
        {
            DoneSummary.Text = $"Converted {outputs} {(outputs == 1 ? "file" : "files")}";
            DoneSummary.Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x8A, 0x3B));
        }
        else if (model.Stopped)
        {
            DoneSummary.Text = "Stopped";
            DoneSummary.Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0x6C, 0x78));
        }
        else if (model.Errors.Count > 0)
        {
            DoneSummary.Text = "Nothing was converted";
            DoneSummary.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        }
        else
        {
            DoneSummary.Text = "";
        }

        TrashSummary.Visibility = model.Trashed > 0 ? Visibility.Visible : Visibility.Collapsed;
        TrashSummary.Text = model.Trashed == 1
            ? "Moved the original to the Recycle Bin"
            : $"Moved {model.Trashed} originals to the Recycle Bin";
        StoppedSummary.Visibility = model.Stopped && outputs > 0 ? Visibility.Visible : Visibility.Collapsed;
        RevealButton.IsEnabled = outputs > 0;

        var installed = QuickAction.IsInstalled;
        MenuHintText.Text = installed
            ? "Convert… is in Explorer's right-click menu"
            : "Add “Convert…” to Explorer's right-click menu";
        MenuHintButton.Content = installed ? "Remove" : "Install";
    }

    private void RebuildPicker()
    {
        syncingPicker = true;
        var view = new ListCollectionView(model.AvailableTargets);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Target.GroupLabel)));
        FormatPicker.ItemsSource = view;
        FormatPicker.SelectedItem = model.SelectedTarget;
        syncingPicker = false;
    }

    private void FormatPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingPicker) return;
        if (FormatPicker.SelectedItem is Target target && !ReferenceEquals(model.SelectedTarget, target))
            model.SelectedTarget = target;
    }

    // MARK: Header icon

    private void ApplyHeaderIcon()
    {
        var key = model.KindIconKey;
        if (key == "app")
        {
            HeaderIcon.Visibility = Visibility.Collapsed;
            HeaderImage.Visibility = Visibility.Visible;
            return;
        }

        HeaderIcon.Visibility = Visibility.Visible;
        HeaderImage.Visibility = Visibility.Collapsed;
        var data = key switch
        {
            "audio" => "M2,9 L2,15 M6,5 L6,19 M10,2 L10,22 M14,6 L14,18 M18,10 L18,14",
            "video" => "M2,4.5 H22 V19.5 H2 Z M7,4.5 V19.5 M17,4.5 V19.5",
            "image" => "M2,4 H22 V20 H2 Z M2,16.5 L8,10.5 L13,15.5 L16.5,12 L22,17.5 M7,8.2 A1.4,1.4 0 1 0 7.01,8.2",
            "pdf" => "M6,2 H15 L20,7 V22 H6 Z M15,2 V7 H20 M9,13 H17 M9,17 H15",
            _ => "M6,2 H15 L20,7 V22 H6 Z M15,2 V7 H20 M9,12 H17 M9,16 H14",
        };
        HeaderIcon.Data = Geometry.Parse(data);
    }

    // MARK: Settings panel

    private void RebuildSettings()
    {
        var target = model.SelectedTarget;
        var hasSettings = target?.HasSettings() == true;
        AdvancedToggle.Visibility = hasSettings ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = hasSettings && model.ShowAdvanced ? Visibility.Visible : Visibility.Collapsed;

        if (target is null || !hasSettings)
        {
            SettingsPanel.Children.Clear();
            builtForTarget = target;
            return;
        }
        if (ReferenceEquals(target, builtForTarget) && SettingsPanel.Children.Count > 0) return;

        builtForTarget = target;
        SettingsPanel.Children.Clear();
        var s = model.Settings;

        switch (target.Group)
        {
            case OutGroup.Audio:
                BuildAudioSettings(target, s);
                break;
            case OutGroup.Video when target.Id == "gif":
                BuildGifSettings(s);
                break;
            case OutGroup.Video:
                BuildVideoSettings(target, s);
                break;
            case OutGroup.Image:
                BuildImageSettings(target, s);
                break;
            case OutGroup.Document:
                if (target.Id == "pdf-doc") AddRow("Paper", Picker([(1, "A4"), (0, "US Letter")], s.PaperA4 ? 1 : 0, v => s.PaperA4 = v == 1));
                break;
            case OutGroup.PdfPages:
                BuildPdfSettings(target, s);
                break;
        }
    }

    private void BuildAudioSettings(Target target, ConvSettings s)
    {
        if (target.IsLossyAudio())
        {
            AddRow("Bitrate", Picker(
                target.BitrateOptions().Select(b => (b, b == 0 ? "VBR – best (~245 kbps)" : $"{b} kbps")),
                s.AudioBitrate, v => s.AudioBitrate = v));
        }
        if (target.IsLosslessAudio())
        {
            AddRow("Bit depth", Picker([(0, "Match source"), (16, "16-bit"), (24, "24-bit")], s.BitDepth, v => s.BitDepth = v));
        }
        var rates = target.SampleRates();
        if (rates.Length > 0)
        {
            AddRow("Sample rate", Picker(
                rates.Select(r => (r, r == 0 ? "Same as source" : RateLabel(r))), s.SampleRate, v => s.SampleRate = v));
        }
        if (target.HasChannelOption())
        {
            AddRow("Channels", Picker([(0, "Same as source"), (2, "Stereo"), (1, "Mono")], s.Channels, v => s.Channels = v));
        }
    }

    private void BuildVideoSettings(Target target, ConvSettings s)
    {
        if (target.Id != "mov-prores")
        {
            AddRow("Quality", SliderRow(0, 1, s.VideoQuality, v => s.VideoQuality = v, "Smaller", "Better"));
        }
        AddRow("Resolution", Picker(
            [(0, "Same as source"), (2160, "4K (2160p)"), (1440, "1440p"), (1080, "1080p"), (720, "720p"), (480, "480p")],
            s.VideoHeight, v => s.VideoHeight = v));
        AddRow("Frame rate", Picker(
            [(0, "Same as source"), (60, "60 fps"), (30, "30 fps"), (25, "25 fps"), (24, "24 fps")],
            s.Fps, v => s.Fps = v));

        var checks = new StackPanel();
        checks.Children.Add(Check("Include audio", s.KeepAudio, v => s.KeepAudio = v));
        if (target.HasHardwareOption())
        {
            checks.Children.Add(Check("Hardware encoding (much faster)", s.Hardware, v => s.Hardware = v));
        }
        AddRow("", checks);
    }

    private void BuildGifSettings(ConvSettings s)
    {
        AddRow("Width", Picker([(320, "320 px"), (480, "480 px"), (640, "640 px"), (800, "800 px"), (1080, "1080 px")],
            s.GifWidth, v => s.GifWidth = v));
        AddRow("Frame rate", Picker([(10, "10 fps"), (12, "12 fps"), (15, "15 fps"), (20, "20 fps"), (25, "25 fps"), (30, "30 fps")],
            s.GifFps, v => s.GifFps = v));
    }

    private void BuildImageSettings(Target target, ConvSettings s)
    {
        if (target.IsLossyImage()) AddRow("Quality", SliderRow(0.3, 1, s.ImageQuality, v => s.ImageQuality = v));
        AddRow("Max size", Picker(
            [(0, "Same as source"), (4096, "4096 px"), (2048, "2048 px"), (1920, "1920 px"), (1280, "1280 px"), (1080, "1080 px"), (512, "512 px")],
            s.MaxSize, v => s.MaxSize = v));
    }

    private void BuildPdfSettings(Target target, ConvSettings s)
    {
        AddRow("Resolution", Picker(
            [(72, "72 dpi (screen)"), (150, "150 dpi"), (300, "300 dpi (print)")], s.PdfDpi, v => s.PdfDpi = v));
        if (target.Id == "pdf-jpg") AddRow("Quality", SliderRow(0.3, 1, s.ImageQuality, v => s.ImageQuality = v));
    }

    // MARK: Settings controls

    private void AddRow(string label, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var text = new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("TextSecondary"),
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        Grid.SetColumn(control, 1);
        if (control is ComboBox combo) combo.HorizontalAlignment = HorizontalAlignment.Left;
        if (control is StackPanel panel) panel.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(control);
        SettingsPanel.Children.Add(grid);
    }

    private ComboBox Picker(IEnumerable<(int Value, string Label)> options, int selected, Action<int> changed)
    {
        var list = options.ToList();
        var box = new ComboBox
        {
            Style = (Style)FindResource("Picker"),
            ItemsSource = list.Select(o => o.Label).ToList(),
            SelectedIndex = Math.Max(0, list.FindIndex(o => o.Value == selected)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.SelectionChanged += (_, _) =>
        {
            var index = box.SelectedIndex;
            if (index < 0 || index >= list.Count) return;
            changed(list[index].Value);
            model.SaveSettings();
        };
        return box;
    }

    private FrameworkElement SliderRow(double min, double max, double value, Action<double> set,
        string? leftLabel = null, string? rightLabel = null)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = leftLabel is null ? 180 : 150,
            TickFrequency = 0.05,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var percent = new TextBlock
        {
            Text = $"{Math.Round(value * 100)}%",
            Width = 42,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextSecondary"),
            FontSize = 12,
        };
        slider.ValueChanged += (_, e) =>
        {
            percent.Text = $"{Math.Round(e.NewValue * 100)}%";
            set(e.NewValue);
            model.SaveSettings();
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (leftLabel is not null)
            panel.Children.Add(new TextBlock
            {
                Text = leftLabel, FontSize = 11.5, Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("TextSecondary"),
            });
        panel.Children.Add(slider);
        panel.Children.Add(percent);
        if (rightLabel is not null)
            panel.Children.Add(new TextBlock
            {
                Text = rightLabel, FontSize = 11.5, Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("TextSecondary"),
            });
        return panel;
    }

    private CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 0, 0, 5) };
        box.Checked += (_, _) => { set(true); model.SaveSettings(); };
        box.Unchecked += (_, _) => { set(false); model.SaveSettings(); };
        return box;
    }

    private static string RateLabel(int hz)
    {
        var k = hz / 1000.0;
        return k == Math.Round(k) ? $"{k:0} kHz" : $"{k:0.0} kHz";
    }

    // MARK: Commands

    private void ChooseFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose files to convert",
            Multiselect = true,
            Filter = "Supported files|" + SupportedFilter + "|All files|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) model.AddFiles(dialog.FileNames);
    }

    private static readonly string SupportedFilter = string.Join(";",
        new[] { "*.mp3", "*.wav", "*.flac", "*.m4a", "*.aac", "*.ogg", "*.opus", "*.wma", "*.aiff", "*.mp4",
                "*.mov", "*.mkv", "*.webm", "*.avi", "*.wmv", "*.flv", "*.m4v", "*.ts", "*.mts", "*.3gp",
                "*.jpg", "*.jpeg", "*.png", "*.gif", "*.bmp", "*.tif", "*.tiff", "*.heic", "*.heif", "*.avif",
                "*.webp", "*.ico", "*.dng", "*.cr2", "*.nef", "*.arw",
                "*.pdf", "*.docx", "*.doc", "*.odt", "*.rtf", "*.html", "*.htm", "*.txt", "*.md", "*.markdown" });

    private async void Convert_Click(object sender, RoutedEventArgs e) => await model.StartAsync();

    private void Stop_Click(object sender, RoutedEventArgs e) => model.Cancel();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    private void Reveal_Click(object sender, RoutedEventArgs e) => Fs.Reveal(model.Outputs);

    private void MenuHint_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (QuickAction.IsInstalled) QuickAction.Uninstall();
            else QuickAction.Install();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Convert", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Refresh();
    }

    // MARK: Drag & drop

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && model.Phase != Phase.Running
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (model.Phase == Phase.Running) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) model.AddFiles(paths);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (model.Phase == Phase.Running) model.Cancel();
        base.OnClosing(e);
    }
}
