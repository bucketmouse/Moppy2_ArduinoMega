using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moppy.Control.Views;
using Moppy.Core.Comms;
using Moppy.Core.Comms.Bridge;
using Moppy.Core.Control;
using Moppy.Core.Status;

namespace Moppy.Control.Tests;

/// <summary>
/// Drives the real main window (rendered headlessly with Skia) against a controller wired to a fake network.
/// </summary>
public class MainWindowTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("moppy-ui-tests").FullName;

    public void Dispose() => Directory.Delete(tempDir, recursive: true);

    /// <summary>Set MOPPY_SCREENSHOT_DIR to save rendered frames.</summary>
    private static void Screenshot(Window window, string name)
    {
        string? dir = Environment.GetEnvironmentVariable("MOPPY_SCREENSHOT_DIR");
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
            window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    private (MoppyController Controller, FakeBridge Bridge, MainWindow Window) Open()
    {
        var bus = new StatusBus();
        var network = new NetworkManager(bus, includeUdp: true, () => ["COM3"]);
        var bridge = new FakeBridge("FAKE");
        network.AddBridge(bridge);
        var controller = new MoppyController(MoppyPreferences.Load(Path.Combine(tempDir, "config.json")), network, bus);
        var window = new MainWindow(controller);
        window.Show();
        Pump();
        return (controller, bridge, window);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static T Find<T>(Avalonia.Controls.Control root, string name)
        where T : Avalonia.Controls.Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    /// <summary>Focuses a text box then another control, as a user tabbing out of it would.</summary>
    private static void MoveFocusAway(TextBox textBox, InputElement next)
    {
        textBox.Focus();
        Pump();
        next.Focus();
        Pump();
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the UI");
            Pump();
            Thread.Sleep(10);
        }
    }

    [AvaloniaFact]
    public void ConnectingABridgeShowsDiscoveredDevices()
    {
        var (controller, bridge, window) = Open();
        var network = Find<NetworkPanel>(window, "NetworkPanel");
        var bridgeChecks = network.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(["239.2.2.7:30994", "COM3", "FAKE"], bridgeChecks.Select(c => c.Content as string));

        // Serial bridges offer baud rates, defaulting to 57600
        var baud = network.GetVisualDescendants().OfType<ComboBox>().Single();
        Assert.Equal(57600, baud.SelectedItem);

        var fakeCheck = bridgeChecks.Single(c => (string?)c.Content == "FAKE");
        fakeCheck.IsChecked = true;
        fakeCheck.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.True(bridge.IsConnected);

        bridge.Receive(MoppyMessageFactory.SystemPong(1, 1, 8));
        PumpUntil(() => network.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "FAKE - remote"));
        var deviceFields = network.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("deviceField")).Select(t => t.Text).ToList();
        Assert.Equal(["1", "1", "8"], deviceFields);
        Screenshot(window, "device-connected");

        // Connecting a port that can't be opened shows the error and leaves it unchecked
        var comCheck = Find<NetworkPanel>(window, "NetworkPanel").GetVisualDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "COM3");
        comCheck.IsChecked = true;
        comCheck.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        var error = Find<TextBlock>(network, "ErrorText");
        Assert.True(error.IsVisible);
        Assert.Contains("COM3", error.Text);
        Assert.False(controller.NetworkManager.AvailableNetworkBridges["COM3"].IsConnected);

        window.Close();
        controller.Dispose();
    }

    [AvaloniaFact]
    public void MapperPanelsEditScripts()
    {
        var (controller, _, window) = Open();
        var collection = Find<MapperCollectionPanel>(window, "MapperCollectionPanel");
        Assert.Single(collection.GetVisualDescendants().OfType<MapperPanel>());

        Find<Button>(collection, "AddMapperButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        var panels = collection.GetVisualDescendants().OfType<MapperPanel>().ToList();
        Assert.Equal(2, panels.Count);

        // Choose a custom note script, type a valid then an invalid script
        var panel = panels[1];
        var combos = panel.GetVisualDescendants().OfType<ComboBox>().ToList();
        var scripts = panel.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("script")).ToList();
        Assert.Equal(4, scripts.Count);
        Assert.True(scripts[3].IsReadOnly);
        combos[3].SelectedItem = MapperPresets.CustomChoice;
        Pump();
        Assert.False(scripts[3].IsReadOnly);

        scripts[3].Text = "n+12";
        MoveFocusAway(scripts[3], combos[0]);
        Assert.Equal("n+12", controller.ConfiguredMappers[1].Mapper.NoteScript);
        Assert.DoesNotContain("invalid", scripts[3].Classes);

        scripts[3].Text = "n +";
        MoveFocusAway(scripts[3], combos[0]);
        Assert.Contains("invalid", scripts[3].Classes);
        Assert.Equal("n+12", controller.ConfiguredMappers[1].Mapper.NoteScript);

        // Choosing a preset applies it
        combos[2].SelectedItem = "Round Robin (8 drives)";
        Pump();
        Assert.Equal("nextRoundRobinSubAddress(8)", scripts[2].Text);
        Assert.Equal("nextRoundRobinSubAddress(8)", controller.ConfiguredMappers[1].Mapper.SubAddressScript);
        Screenshot(window, "mappers");

        // Delete the first mapper
        Find<Button>(panels[0], "DeleteMapperButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.Single(controller.ConfiguredMappers);
        Assert.Single(collection.GetVisualDescendants().OfType<MapperPanel>());

        // Closing saves the mappers and window layout
        window.Close();
        controller.Dispose();
        var saved = MoppyPreferences.Load(Path.Combine(tempDir, "config.json")).Configuration;
        Assert.Single(saved.MapperConfigs);
        Assert.Equal("n+12", saved.MapperConfigs[0].NoteCustomScript);
        Assert.Equal("Round Robin (8 drives)", saved.MapperConfigs[0].SubAddressChoice);
    }

    [AvaloniaFact]
    public void PlayingUpdatesControlsAndLocksMappers()
    {
        var (controller, bridge, window) = Open();
        controller.NetworkManager.ConnectBridge("FAKE", null);
        string song = Path.Combine(AppContext.BaseDirectory, "samplesongs", "Tetris.mid");
        controller.Playlist.Add(song);
        Pump();

        var sequencer = Find<SequencerPanel>(window, "SequencerPanel");
        Assert.Equal(["Tetris.mid"], Find<ListBox>(sequencer, "PlaylistList").ItemsSource!.Cast<string>());

        Find<Slider>(sequencer, "VolumeSlider").Value = 50;
        Pump();
        Assert.Equal("50% Volume", Find<TextBlock>(sequencer, "VolumeLabel").Text);
        Assert.Equal(0.5, controller.PostProcessor.VelocityMultiplier);

        Find<Button>(sequencer, "PlayButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => Find<TextBlock>(sequencer, "TotalTimeLabel").Text == "1:35");
        Assert.True(controller.Sequencer.IsPlaying);
        PumpUntil(() => !Find<Button>(window, "AddMapperButton").IsEnabled);
        Assert.Equal(0, Find<ListBox>(sequencer, "PlaylistList").SelectedIndex);

        // Tempo follows the song, and can be changed
        Assert.Equal(100m, Find<NumericUpDown>(sequencer, "TempoSpinner").Value); // Tetris.mid is 100 BPM
        Find<NumericUpDown>(sequencer, "TempoSpinner").Value = 200;
        Assert.Equal(200f, controller.Sequencer.Tempo);

        PumpUntil(() => bridge.Sent.Any(m => m.MessageBody.Length > 2 && m.MessageCommandByte == CommandByte.DevPlayNote));
        var firstNote = bridge.Sent.First(m => m.MessageCommandByte == CommandByte.DevPlayNote);
        Assert.True(firstNote.MessageCommandPayload[1] <= 64); // Volume applied
        Screenshot(window, "playing");

        Find<Button>(sequencer, "StopButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => Find<Button>(window, "AddMapperButton").IsEnabled);
        Assert.False(controller.Sequencer.IsPlaying);
        Assert.Equal("0:00", Find<TextBlock>(sequencer, "CurrentTimeLabel").Text);
        Assert.Equal(MoppyMessage.SysReset, bridge.Sent[^1]);

        window.Close();
        controller.Dispose();
    }

    private sealed class FakeBridge(string id) : NetworkBridge
    {
        private readonly List<MoppyMessage> sent = [];
        private bool connected;

        public IReadOnlyList<MoppyMessage> Sent
        {
            get
            {
                lock (sent)
                {
                    return sent.ToArray();
                }
            }
        }

        public override void Connect(object? connectionOption) => connected = true;

        public override void Connect() => connected = true;

        public override bool IsConnected => connected;

        public override void SendMessage(MoppyMessage messageToSend)
        {
            lock (sent)
            {
                sent.Add(messageToSend);
            }
        }

        public override string NetworkIdentifier => id;

        public override IReadOnlyList<object> ConnectionOptions => [];

        public override object? CurrentConnectionOption => null;

        public override void Close() => connected = false;

        public void Receive(MoppyMessage message) =>
            AcceptNetworkMessage(MoppyMessageFactory.NetworkReceivedFromBytes(message.MessageBytes, "Fake", id, "remote"));
    }
}
