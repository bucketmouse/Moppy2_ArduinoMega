using Microsoft.Extensions.Logging;
using Moppy.Core.Events.Mapper;
using Moppy.Core.Midi;
using Moppy.Core.Status;

namespace Moppy.Core.Control;

/// <summary>
/// The complete Moppy controller: everything the Moppy Control application does, wired together.
/// </summary>
/// <remarks>
/// <para>Composes a <see cref="Status.StatusBus"/>, <see cref="Control.NetworkManager"/>, mappers, a
/// <see cref="VelocityPostProcessor"/>, a <see cref="MoppyMidiSequencer"/> with a <see cref="Control.Playlist"/>,
/// and optional MIDI In / MIDI Out devices.  Call <see cref="Start"/> to begin discovering devices.</para>
/// <para>Register an <see cref="IStatusConsumer"/> with <see cref="StatusBus"/> to follow playback and network
/// changes.  Updates may arrive on background threads.</para>
/// </remarks>
public sealed class MoppyController : IDisposable
{
    private readonly object mappersLock = new();
    private readonly object midiDevicesLock = new();
    private List<ConfiguredMapper> configuredMappers = [];
    private MidiInputConnection? midiIn;
    private MidiOutputReceiver? midiOut;
    private bool disposed;

    /// <param name="preferences">Saved configuration (see <see cref="MoppyPreferences.Load"/>).</param>
    /// <param name="networkManager">Network manager to use; by default one is created offering UDP and every serial port.</param>
    /// <param name="statusBus">Status bus to use (must be the one <paramref name="networkManager"/> reports to, if given).</param>
    public MoppyController(MoppyPreferences preferences, NetworkManager? networkManager = null, StatusBus? statusBus = null)
    {
        Preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        StatusBus = statusBus ?? new StatusBus();
        NetworkManager = networkManager ?? new NetworkManager(StatusBus);
        ReceiverSender = new MoppyMidiReceiverSender(Mappers, PostProcessor, NetworkManager.PrimaryBridge);
        Sequencer = new MoppyMidiSequencer(StatusBus, ReceiverSender) { AutoReset = Config.AutoReset };
        Playlist = new Playlist(Sequencer);
        StatusBus.RegisterConsumer(Playlist);
        StatusBus.RegisterConsumer(new MapperSaver(this));

        LoadMappersFromConfig(Config.MapperConfigs);
    }

    private static ILogger Log => MoppyLogging.CreateLogger<MoppyController>();

    public MoppyPreferences Preferences { get; }

    public MoppyConfig Config => Preferences.Configuration;

    /// <summary>Status updates for local components (sequencer and network changes).</summary>
    public StatusBus StatusBus { get; }

    public NetworkManager NetworkManager { get; }

    /// <summary>The mappers applied to every MIDI event (from the sequencer and MIDI In).</summary>
    public MapperCollection<MidiMessage> Mappers { get; } = new();

    /// <summary>Volume control.</summary>
    public VelocityPostProcessor PostProcessor { get; } = new();

    /// <summary>Maps MIDI to the network.  Send MIDI messages here to play them on the network.</summary>
    public MoppyMidiReceiverSender ReceiverSender { get; }

    public MoppyMidiSequencer Sequencer { get; }

    public Playlist Playlist { get; }

    /// <summary>Raised when mappers are added or removed.</summary>
    public event EventHandler? MappersChanged;

    /// <summary>Starts discovering devices on the network.</summary>
    public void Start() => NetworkManager.Start();

    /// <summary>Resets devices when a sequence finishes playing.  Saved in the config.</summary>
    public bool AutoReset
    {
        get => Sequencer.AutoReset;
        set
        {
            Sequencer.AutoReset = value;
            Config.AutoReset = value;
        }
    }

    ////
    // Mappers
    ////

    /// <summary>The configurable script mappers, in order.</summary>
    public IReadOnlyList<ConfiguredMapper> ConfiguredMappers
    {
        get
        {
            lock (mappersLock)
            {
                return configuredMappers.ToArray();
            }
        }
    }

    /// <summary>Adds a script mapper (with default scripts if <paramref name="config"/> is null).</summary>
    public ConfiguredMapper AddMapper(MidiScriptMapperConfig? config = null)
    {
        var mapper = new ConfiguredMapper(config ?? new MidiScriptMapperConfig());
        lock (mappersLock)
        {
            configuredMappers = [.. configuredMappers, mapper];
            Mappers.AddMapper(mapper.Mapper);
        }

        MappersChanged?.Invoke(this, EventArgs.Empty);
        return mapper;
    }

    public void RemoveMapper(ConfiguredMapper mapper)
    {
        lock (mappersLock)
        {
            configuredMappers = configuredMappers.Where(m => m != mapper).ToList();
            Mappers.RemoveMapper(mapper.Mapper);
        }

        MappersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces all mappers with ones created from the given configs.</summary>
    public void LoadMappersFromConfig(IEnumerable<MidiScriptMapperConfig> configs)
    {
        var newMappers = configs.Select(c => new ConfiguredMapper(c.Clone())).ToList();
        lock (mappersLock)
        {
            Mappers.ClearMappers();
            configuredMappers = newMappers;
            foreach (var mapper in newMappers)
            {
                Mappers.AddMapper(mapper.Mapper);
            }
        }

        MappersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stores the current mappers in the config and saves the configuration.</summary>
    public void SaveMappersToConfig()
    {
        Config.MapperConfigs = ConfiguredMappers.Select(m => m.Config.Clone()).ToList();
        Preferences.Save();
    }

    ////
    // MIDI devices
    ////

    /// <summary>Names of MIDI In devices that can be selected.</summary>
    public IReadOnlyList<string> GetMidiInDevices() => MidiDevices.GetInputDeviceNames();

    /// <summary>Names of MIDI Out devices that can be selected.</summary>
    public IReadOnlyList<string> GetMidiOutDevices() => MidiDevices.GetOutputDeviceNames();

    public string? CurrentMidiInDevice
    {
        get
        {
            lock (midiDevicesLock)
            {
                return midiIn?.Name;
            }
        }
    }

    public string? CurrentMidiOutDevice
    {
        get
        {
            lock (midiDevicesLock)
            {
                return midiOut?.Name;
            }
        }
    }

    /// <summary>
    /// Selects the MIDI device to receive events from (they are mapped and played on the network), or null for none.
    /// </summary>
    /// <exception cref="MidiUnavailableException">If the device can't be opened (MIDI In is then disabled).</exception>
    public void SelectMidiInDevice(string? deviceName)
    {
        lock (midiDevicesLock)
        {
            // If we'd previously selected a MIDI In device, close it so we're not getting events from it.
            midiIn?.Dispose();
            midiIn = null;
            if (deviceName is not null)
            {
                midiIn = MidiDevices.OpenInput(deviceName, ReceiverSender);
            }
        }
    }

    /// <summary>
    /// Selects a MIDI device to send all raw MIDI events to (MIDI thru), or null for none.
    /// </summary>
    /// <exception cref="MidiUnavailableException">If the device can't be opened (MIDI Out is then disabled).</exception>
    public void SelectMidiOutDevice(string? deviceName)
    {
        lock (midiDevicesLock)
        {
            ReceiverSender.MidiThru = null; // Disable thru sending
            midiOut?.Dispose();
            midiOut = null;
            if (deviceName is not null)
            {
                midiOut = MidiDevices.OpenOutput(deviceName);
                ReceiverSender.MidiThru = midiOut;
            }
        }
    }

    /// <summary>Stops playback and network activity, and closes all devices.  The configuration is not saved.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        TryDispose(Sequencer);
        TryDispose(() => SelectMidiInDevice(null));
        TryDispose(() => SelectMidiOutDevice(null));
        TryDispose(NetworkManager);
    }

    private static void TryDispose(IDisposable disposable) => TryDispose(disposable.Dispose);

    private static void TryDispose(Action dispose)
    {
        try
        {
            dispose();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Error during shutdown");
        }
    }

    /// <summary>Proactively saves the mappers when playback starts.</summary>
    private sealed class MapperSaver(MoppyController controller) : IStatusConsumer
    {
        public void ReceiveUpdate(StatusUpdate update)
        {
            if (update.Type == StatusType.SequenceStart)
            {
                controller.SaveMappersToConfig();
            }
        }
    }
}
