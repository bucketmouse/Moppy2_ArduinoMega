using Moppy.Core.Comms;
using Moppy.Core.Control;
using Moppy.Core.Device;
using Moppy.Core.Midi;
using Moppy.Core.Status;
using static Moppy.Core.Tests.TestHelpers;

namespace Moppy.Core.Tests;

public class ControlTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("moppy-tests").FullName;

    public void Dispose() => Directory.Delete(tempDir, recursive: true);

    private string WriteMidi(string name, params MidiEvent[] events)
    {
        // A tiny single-track file at 120 BPM, 100 ticks per quarter (5ms per tick)
        var body = new List<byte>();
        long lastTick = 0;
        foreach (var e in events)
        {
            body.AddRange(MidiFileReader.EncodeVariableLength(e.Tick - lastTick));
            body.AddRange(e.Message.ToArray());
            lastTick = e.Tick;
        }

        body.AddRange([0x00, 0xFF, 0x2F, 0x00]);
        string path = Path.Combine(tempDir, name);
        File.WriteAllBytes(path, MidiFileTests.BuildFile(100, body.ToArray()));
        return path;
    }

    private static MoppyController CreateController(FakeBridge bridge, out StatusRecorder recorder)
    {
        var bus = new StatusBus();
        var network = new NetworkManager(bus, includeUdp: false, () => []);
        network.AddBridge(bridge);
        network.ConnectBridge(bridge.NetworkIdentifier, null);
        var controller = new MoppyController(MoppyPreferences.InMemory(), network, bus);
        recorder = new StatusRecorder();
        bus.RegisterConsumer(recorder);
        return controller;
    }

    [Fact]
    public void ControllerPlaysThePlaylistToTheNetwork()
    {
        var bridge = new FakeBridge("fake");
        using var controller = CreateController(bridge, out var recorder);
        controller.Playlist.Add(WriteMidi("one.mid", new MidiEvent(0, NoteOn(0, 60, 100)), new MidiEvent(10, NoteOff(0, 60))));
        controller.Playlist.Add(Path.Combine(tempDir, "missing.mid"));
        controller.Playlist.Add(WriteMidi("two.mid", new MidiEvent(0, NoteOn(1, 64, 80)), new MidiEvent(10, NoteOff(1, 64))));
        controller.AutoReset = true;

        controller.Playlist.TogglePlayPause();
        Assert.True(recorder.WaitFor(StatusType.SequenceStopped, TimeSpan.FromSeconds(10)));

        // Song one, (missing file skipped), song two, then back to the start and stopped
        var sent = bridge.Sent.Select(m => m.ToArray()).ToList();
        Assert.Equal(
        [
            MoppyMessage.SysStart.ToArray(),
            MoppyMessageFactory.DevicePlayNote(1, 1, 60, 100).ToArray(),
            MoppyMessageFactory.DeviceStopNote(1, 1, 60).ToArray(),
            MoppyMessage.SysStop.ToArray(), MoppyMessage.SysReset.ToArray(), // End (auto reset)
            MoppyMessage.SysStart.ToArray(),
            MoppyMessageFactory.DevicePlayNote(1, 2, 64, 80).ToArray(),
            MoppyMessageFactory.DeviceStopNote(1, 2, 64).ToArray(),
            MoppyMessage.SysStop.ToArray(), MoppyMessage.SysReset.ToArray(), // End (auto reset)
            MoppyMessage.SysStop.ToArray(), MoppyMessage.SysReset.ToArray(), // Stopped
        ], sent);
        Assert.Equal(0, controller.Playlist.LoadedIndex);
        Assert.False(controller.Sequencer.IsPlaying);
    }

    [Fact]
    public void RepeatLoopsThePlaylistAndVolumeApplies()
    {
        var bridge = new FakeBridge("fake");
        using var controller = CreateController(bridge, out var recorder);
        controller.Playlist.Add(WriteMidi("one.mid", new MidiEvent(0, NoteOn(0, 60, 100)), new MidiEvent(4, NoteOff(0, 60))));
        controller.Playlist.Repeat = true;
        controller.PostProcessor.VelocityMultiplier = 0.5;

        controller.Playlist.TogglePlayPause();
        Assert.True(SpinWait.SpinUntil(() => bridge.Sent.Count(m => m.Equals(MoppyMessage.SysStart)) >= 3, TimeSpan.FromSeconds(10)));
        controller.Playlist.TogglePlayPause(); // Pause
        Assert.False(controller.Sequencer.IsPlaying);
        Assert.Contains(MoppyMessageFactory.DevicePlayNote(1, 1, 60, 50), bridge.Sent);
        Assert.Contains(StatusType.SequencePause, recorder.Types);
    }

    [Fact]
    public void PreferencesRoundTripMappers()
    {
        string path = Path.Combine(tempDir, "config.json");
        var bridge = new FakeBridge("fake");
        using (var controller = new MoppyController(MoppyPreferences.Load(path), new NetworkManager(new StatusBus(), false, () => [])))
        {
            Assert.Single(controller.ConfiguredMappers); // Default mapper
            var mapper = controller.AddMapper();
            mapper.SelectChoice(MapperField.Note, MapperPresets.CustomChoice);
            mapper.SetCustomScript(MapperField.Note, "n-12");
            controller.Config.MainWindowSize = new WindowSize(1300, 700);
            controller.SaveMappersToConfig();
        }

        var reloaded = MoppyPreferences.Load(path);
        Assert.Equal(new WindowSize(1300, 700), reloaded.Configuration.MainWindowSize);
        Assert.Equal(2, reloaded.Configuration.MapperConfigs.Count);
        Assert.Equal("n-12", reloaded.Configuration.MapperConfigs[1].NoteCustomScript);
        Assert.Contains("\"noteChoice\": \"Custom\"", File.ReadAllText(path));

        using var restored = new MoppyController(reloaded, new NetworkManager(new StatusBus(), false, () => []));
        Assert.Equal("n-12", restored.ConfiguredMappers[1].Mapper.NoteScript);
    }

    [Fact]
    public void CorruptPreferencesFallBackToDefaults()
    {
        string path = Path.Combine(tempDir, "config.json");
        File.WriteAllText(path, "{ not json");
        var prefs = MoppyPreferences.Load(path);
        Assert.Single(prefs.Configuration.MapperConfigs);
        Assert.Equal(300, prefs.Configuration.MainWindowDividerPosition);
    }

    [Fact]
    public void NetworkManagerDiscoversDevicesFromPongs()
    {
        var bus = new StatusBus();
        var recorder = new StatusRecorder();
        bus.RegisterConsumer(recorder);
        var bridge = new FakeBridge("COM9");
        using var network = new NetworkManager(bus, includeUdp: false, () => []);
        network.AddBridge(bridge);
        network.ConnectBridge("COM9", null);
        Assert.True(network.PrimaryBridge.IsConnected);

        bridge.Receive(MoppyMessageFactory.SystemPong(1, 1, 8));
        bridge.Receive(MoppyMessageFactory.SystemPong(1, 1, 8)); // Same device again
        bridge.Receive(MoppyMessage.SysPing);                     // Not a pong

        Assert.Equal([new DeviceDescriptor("COM9 - remote", 1, 1, 8)], network.RecentlySeenDevices);
        Assert.Equal(1, recorder.Types.Count(t => t == StatusType.NetDevicesChanged));

        network.CloseBridge("COM9");
        Assert.False(bridge.IsConnected);
        Assert.Equal(MoppyMessage.SysStop, bridge.Sent.LastOrDefault() ?? MoppyMessage.SysStop);
        Assert.Throws<ArgumentException>(() => network.ConnectBridge("nope", null));
    }

    [Fact]
    public void RefreshAddsAndRemovesSerialPorts()
    {
        var ports = new List<string> { "COM1" };
        var bus = new StatusBus();
        using var network = new NetworkManager(bus, includeUdp: true, () => ports.ToArray());
        Assert.Equal(["239.2.2.7:30994", "COM1"], network.AvailableNetworkBridges.Keys);

        ports.Remove("COM1");
        ports.Add("COM4");
        network.RefreshAvailableBridges();
        Assert.Equal(["239.2.2.7:30994", "COM4"], network.AvailableNetworkBridges.Keys);
    }

    [Fact]
    public void StatusBusDeliversNestedUpdatesInOrder()
    {
        var bus = new StatusBus();
        var log = new List<string>();
        bus.RegisterConsumer(new DelegateConsumer(u =>
        {
            log.Add($"first:{u.Type}");
            if (u.Type == StatusType.SequenceEnd)
            {
                bus.ReceiveUpdate(StatusUpdate.SequenceStart);
            }
        }));
        bus.RegisterConsumer(new DelegateConsumer(u => log.Add($"second:{u.Type}")));
        bus.RegisterConsumer(new DelegateConsumer(_ => throw new InvalidOperationException("ignored")));

        bus.ReceiveUpdate(StatusUpdate.SequenceEnd(false));
        Assert.Equal(["first:SequenceEnd", "second:SequenceEnd", "first:SequenceStart", "second:SequenceStart"], log);
    }

    private sealed class DelegateConsumer(Action<StatusUpdate> action) : IStatusConsumer
    {
        public void ReceiveUpdate(StatusUpdate update) => action(update);
    }

    internal sealed class StatusRecorder : IStatusConsumer
    {
        private readonly List<StatusType> types = [];

        public IReadOnlyList<StatusType> Types
        {
            get
            {
                lock (types)
                {
                    return types.ToArray();
                }
            }
        }

        public void ReceiveUpdate(StatusUpdate update)
        {
            lock (types)
            {
                types.Add(update.Type);
            }
        }

        public bool WaitFor(StatusType type, TimeSpan timeout) => SpinWait.SpinUntil(() => Types.Contains(type), timeout);
    }
}
