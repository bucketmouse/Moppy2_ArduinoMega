using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Moppy.Core;
using Moppy.Core.Control;
using Moppy.Core.Midi;
using Moppy.Core.Status;

namespace Moppy.Control.Views;

/// <summary>
/// Playlist, transport controls, volume, tempo, and MIDI In / Out selection.
/// </summary>
public partial class SequencerPanel : UserControl, IStatusConsumer
{
    private const string NoDevice = "None";

    private readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private MoppyController? controller;
    private bool seeking;
    private bool updatingTempo;
    private bool updatingDevices;

    public SequencerPanel()
    {
        InitializeComponent();
        progressTimer.Tick += (_, _) => ShowPosition(Controller.Sequencer.SecondsPosition);
    }

    private MoppyController Controller => controller ?? throw new InvalidOperationException("Not initialized");

    private static ILogger Log => MoppyLogging.LoggerFactory.CreateLogger<SequencerPanel>();

    public void Initialize(MoppyController moppyController)
    {
        controller = moppyController;

        AutoResetCheckBox.IsChecked = controller.AutoReset;
        AutoResetCheckBox.IsCheckedChanged += (_, _) => Controller.AutoReset = AutoResetCheckBox.IsChecked == true;
        RepeatCheckBox.IsCheckedChanged += (_, _) => Controller.Playlist.Repeat = RepeatCheckBox.IsChecked == true;

        AddFileButton.Click += async (_, _) => await AddFilesAsync();
        RemoveFileButton.Click += (_, _) => RemoveSelectedFiles();
        PlaylistList.SelectionChanged += (_, _) => UpdateRemoveButton();
        PlaylistList.DoubleTapped += (_, _) =>
        {
            if (PlaylistList.SelectedIndex >= 0)
            {
                Controller.Playlist.PlayIndex(PlaylistList.SelectedIndex);
            }
        };

        PlayButton.Click += (_, _) => Controller.Playlist.TogglePlayPause();
        StopButton.Click += (_, _) => Controller.Sequencer.Stop();

        VolumeSlider.ValueChanged += (_, _) =>
        {
            VolumeLabel.Text = $"{(int)VolumeSlider.Value}% Volume";
            Controller.PostProcessor.VelocityMultiplier = (int)VolumeSlider.Value / 100.0;
        };
        VolumeOverrideCheckBox.IsCheckedChanged += (_, _) => Controller.PostProcessor.OverrideVelocity = VolumeOverrideCheckBox.IsChecked == true;

        // Seeking: pause while the slider is held, and resume playing from the new position when released
        SequenceSlider.AddHandler(PointerPressedEvent, (_, _) => BeginSeek(), RoutingStrategies.Tunnel, handledEventsToo: true);
        SequenceSlider.AddHandler(PointerReleasedEvent, (_, _) => EndSeek(), RoutingStrategies.Tunnel, handledEventsToo: true);
        SequenceSlider.ValueChanged += (_, _) =>
        {
            if (seeking)
            {
                Seek();
            }
        };

        TempoSpinner.ValueChanged += (_, e) =>
        {
            if (!updatingTempo && e.NewValue is { } tempo)
            {
                Controller.Sequencer.SetTempo((float)tempo);
            }
        };
        TempoSpinner.AddHandler(PointerWheelChangedEvent, OnTempoWheel, RoutingStrategies.Tunnel);

        RefreshMidiDevices();
        MidiInComboBox.SelectionChanged += (_, _) => SelectMidiDevice(MidiInComboBox, Controller.SelectMidiInDevice);
        MidiOutComboBox.SelectionChanged += (_, _) => SelectMidiDevice(MidiOutComboBox, Controller.SelectMidiOutDevice);

        controller.Playlist.FilesChanged += (_, _) => Dispatcher.UIThread.Post(RefreshPlaylist);
        controller.Playlist.LoadedIndexChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            int loaded = Controller.Playlist.LoadedIndex;
            if (loaded >= 0 && loaded < PlaylistList.ItemCount)
            {
                PlaylistList.SelectedIndex = loaded;
            }
        });
        RefreshPlaylist();

        controller.StatusBus.RegisterConsumer(this);
    }

    public void ReceiveUpdate(StatusUpdate update) => Dispatcher.UIThread.Post(() => HandleUpdate(update));

    private void HandleUpdate(StatusUpdate update)
    {
        switch (update.Type)
        {
            case StatusType.SequenceLoad:
                long length = Controller.Sequencer.SecondsLength;
                SequenceSlider.Maximum = Math.Max(1, length);
                TotalTimeLabel.Text = FormatTime(length);
                break;
            case StatusType.SequenceStart:
                PlayIcon.Data = (Avalonia.Media.Geometry)this.FindResource("PauseIcon")!;
                progressTimer.Start();
                RemoveFileButton.IsEnabled = false;
                break;
            case StatusType.SequenceStopped:
                SequenceSlider.Value = 0;
                CurrentTimeLabel.Text = FormatTime(0);
                goto case StatusType.SequencePause;
            case StatusType.SequencePause:
                PlayIcon.Data = (Avalonia.Media.Geometry)this.FindResource("PlayIcon")!;
                progressTimer.Stop();
                UpdateRemoveButton();
                break;
            case StatusType.SequenceTempoChange when update.Data is float tempo:
                updatingTempo = true;
                TempoSpinner.Value = (decimal)Math.Round(tempo, 2);
                updatingTempo = false;
                break;
        }
    }

    private async Task AddFilesAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add MIDI files to the playlist",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("MIDI Files") { Patterns = ["*.mid", "*.midi"] }, FilePickerFileTypes.All],
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(Path.GetFullPath(Controller.Config.FileLoadDirectory)),
        });

        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0)
        {
            return;
        }

        Controller.Playlist.AddRange(paths);
        Controller.Config.FileLoadDirectory = Path.GetDirectoryName(paths[^1]) ?? ".";
        Controller.Preferences.Save();
    }

    private void RemoveSelectedFiles()
    {
        var indices = PlaylistList.Selection.SelectedIndexes.ToList();
        Controller.Playlist.RemoveAt(indices);
    }

    private void RefreshPlaylist()
    {
        PlaylistList.ItemsSource = Controller.Playlist.Files.Select(Path.GetFileName).ToList();
        UpdateRemoveButton();
    }

    private void UpdateRemoveButton() =>
        RemoveFileButton.IsEnabled = PlaylistList.SelectedItems?.Count > 0 && !Controller.Sequencer.IsPlaying;

    private void BeginSeek()
    {
        if (!Controller.Sequencer.IsSequenceLoaded || seeking)
        {
            return;
        }

        seeking = true;
        Controller.Sequencer.Pause();
    }

    private void Seek()
    {
        Controller.Sequencer.SecondsPosition = (long)SequenceSlider.Value;
        CurrentTimeLabel.Text = FormatTime(Controller.Sequencer.SecondsPosition);
    }

    private void EndSeek()
    {
        if (!seeking)
        {
            return;
        }

        seeking = false;
        Seek(); // Update position
        Controller.Sequencer.Play();
    }

    private void ShowPosition(long seconds)
    {
        if (!seeking)
        {
            SequenceSlider.Value = seconds;
            CurrentTimeLabel.Text = FormatTime(seconds);
        }
    }

    private void OnTempoWheel(object? sender, PointerWheelEventArgs e)
    {
        decimal current = TempoSpinner.Value ?? 120;
        decimal newValue = Math.Clamp(current + (decimal)Math.Sign(e.Delta.Y), TempoSpinner.Minimum, TempoSpinner.Maximum);
        Controller.Sequencer.SetTempo((float)newValue);
        e.Handled = true;
    }

    private void RefreshMidiDevices()
    {
        updatingDevices = true;
        MidiInComboBox.ItemsSource = new[] { NoDevice }.Concat(Controller.GetMidiInDevices()).ToList();
        MidiOutComboBox.ItemsSource = new[] { NoDevice }.Concat(Controller.GetMidiOutDevices()).ToList();
        MidiInComboBox.SelectedIndex = 0;
        MidiOutComboBox.SelectedIndex = 0;
        updatingDevices = false;
    }

    private void SelectMidiDevice(ComboBox comboBox, Action<string?> select)
    {
        if (updatingDevices || comboBox.SelectedItem is not string selected)
        {
            return;
        }

        try
        {
            select(selected == NoDevice ? null : selected);
        }
        catch (MidiUnavailableException ex)
        {
            Log.LogError(ex, "Unable to open MIDI device {Device}", selected);
            comboBox.SelectedIndex = 0; // On exception, set menu back to "None"
        }
    }

    private static string FormatTime(long seconds) => $"{seconds / 60}:{seconds % 60:00}";
}
