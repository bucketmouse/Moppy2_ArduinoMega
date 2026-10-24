using Microsoft.Extensions.Logging;
using Moppy.Core.Status;

namespace Moppy.Core.Midi;

/// <summary>
/// Wrapper around <see cref="MidiSequencer"/> for playing MIDI files to a Moppy network.
/// </summary>
/// <remarks>
/// Additionally provides feedback to listeners about the current state of the sequencer via the
/// <see cref="StatusBus"/> (load, start, pause, stop, end, and tempo changes).
/// </remarks>
public sealed class MoppyMidiSequencer : IDisposable
{
    private readonly MidiSequencer seq = new();
    private readonly StatusBus statusBus;

    public MoppyMidiSequencer(StatusBus statusBus, MoppyMidiReceiverSender receiverSender)
    {
        ArgumentNullException.ThrowIfNull(receiverSender);
        this.statusBus = statusBus ?? throw new ArgumentNullException(nameof(statusBus));
        this.statusBus.RegisterConsumer(receiverSender); // Register receiverSender to send seq messages to network
        seq.Receiver = receiverSender;
        seq.MetaEventReceived += OnMetaEvent;
    }

    private static ILogger Log => MoppyLogging.CreateLogger<MoppyMidiSequencer>();

    /// <summary>If true, devices are reset when a sequence finishes playing.</summary>
    public bool AutoReset { get; set; }

    /// <summary>The underlying sequencer.</summary>
    public MidiSequencer Sequencer => seq;

    public MidiSequence? LoadedSequence => seq.Sequence;

    public bool IsPlaying => seq.IsRunning;

    public bool IsSequenceLoaded => seq.Sequence is not null;

    private void OnMetaEvent(MetaMessage meta)
    {
        if (meta.TryGetTempo(out _))
        {
            // The sequencer has already applied the new tempo; let everyone know about it
            statusBus.ReceiveUpdate(StatusUpdate.TempoChange(seq.TempoInBpm));
        }
        else if (meta.Type == MetaMessage.EndOfTrackType)
        {
            seq.TickPosition = 0; // Reset sequencer so we can press "play" again right away
            statusBus.ReceiveUpdate(StatusUpdate.SequenceEnd(AutoReset));
        }
    }

    /// <exception cref="InvalidOperationException">If no sequence is loaded.</exception>
    public void Play()
    {
        if (!IsSequenceLoaded)
        {
            throw new InvalidOperationException("Sequence not set");
        }

        // Announce the start first so devices hear SYS_START before the first note
        statusBus.ReceiveUpdate(StatusUpdate.SequenceStart);
        seq.Start();
    }

    public void Pause()
    {
        seq.Stop();
        statusBus.ReceiveUpdate(StatusUpdate.SequencePause);
    }

    public void Stop()
    {
        seq.Stop();
        seq.TickPosition = 0;
        statusBus.ReceiveUpdate(StatusUpdate.SequenceStopped);
    }

    /// <exception cref="IOException">If the file doesn't exist or can't be read.</exception>
    /// <exception cref="InvalidMidiDataException">If the file isn't a valid MIDI file.</exception>
    public void LoadSequence(string sequenceFile)
    {
        if (!File.Exists(sequenceFile))
        {
            throw new IOException($"File '{Path.GetFullPath(sequenceFile)}' not found, or isn't a file");
        }

        LoadSequence(MidiFileReader.Read(sequenceFile));
    }

    public void LoadSequence(MidiSequence sequenceToLoad)
    {
        ArgumentNullException.ThrowIfNull(sequenceToLoad);
        seq.Sequence = sequenceToLoad;
        statusBus.ReceiveUpdate(StatusUpdate.SequenceLoaded(sequenceToLoad));
        statusBus.ReceiveUpdate(StatusUpdate.TempoChange(seq.TempoInBpm));

        Log.LogInformation("Loaded sequence with {Tracks} tracks at {Tempo} BPM", sequenceToLoad.Tracks.Count, seq.TempoInBpm);
    }

    public long SecondsLength => seq.MicrosecondLength / 1_000_000;

    /// <summary>Playback position in whole seconds.  Setting it keeps the play state.</summary>
    public long SecondsPosition
    {
        get => seq.MicrosecondPosition / 1_000_000;
        set
        {
            float tempoBefore = seq.TempoInBpm;
            seq.MicrosecondPosition = value * 1_000_000;
            if (seq.TempoInBpm != tempoBefore)
            {
                statusBus.ReceiveUpdate(StatusUpdate.TempoChange(seq.TempoInBpm));
            }
        }
    }

    public float Tempo => seq.TempoInBpm;

    /// <summary>Sets the playback tempo in BPM.</summary>
    public void SetTempo(float newTempo)
    {
        // Don't set the tempo again if it's not different (prevents some weird loops in GUIs)
        if (seq.TempoInBpm != newTempo)
        {
            seq.TempoInBpm = newTempo;
            statusBus.ReceiveUpdate(StatusUpdate.TempoChange(newTempo));
            Log.LogInformation("Tempo changed to {Tempo}", newTempo);
        }
    }

    public void Dispose() => seq.Dispose();
}
