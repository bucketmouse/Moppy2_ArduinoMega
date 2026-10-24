using Moppy.Core.Midi;

namespace Moppy.Core.Status;

public enum StatusType
{
    // Sequencer statuses
    SequenceLoad,
    SequenceStart,
    SequencePause,
    /// <summary>Sequence playback was stopped manually.</summary>
    SequenceStopped,
    /// <summary>Sequence playback stopped because it reached the end of the sequence.</summary>
    SequenceEnd,
    SequenceTempoChange,

    // Network statuses
    /// <summary>A NetworkBridge has either connected or disconnected.</summary>
    NetStatusChanged,
    /// <summary>A device has either become available or unavailable on the network.</summary>
    NetDevicesChanged,
}

/// <summary>
/// A status update sent from a sequencer or network.
/// </summary>
/// <remarks>
/// <see cref="Data"/> depends on the type: a <see cref="MidiSequence"/> for <see cref="StatusType.SequenceLoad"/>,
/// the new tempo in BPM (<see cref="float"/>) for <see cref="StatusType.SequenceTempoChange"/>, and whether
/// devices should be reset (<see cref="bool"/>) for <see cref="StatusType.SequenceStopped"/> and
/// <see cref="StatusType.SequenceEnd"/>.
/// </remarks>
public sealed class StatusUpdate
{
    private StatusUpdate(StatusType type, object? data)
    {
        Type = type;
        Data = data;
    }

    public StatusType Type { get; }

    public object? Data { get; }

    // Sequencer statuses

    public static readonly StatusUpdate SequenceStart = new(StatusType.SequenceStart, null);
    public static readonly StatusUpdate SequencePause = new(StatusType.SequencePause, null);
    public static readonly StatusUpdate SequenceStopped = new(StatusType.SequenceStopped, true); // Always reset when stopped

    public static StatusUpdate SequenceEnd(bool doReset) => new(StatusType.SequenceEnd, doReset);

    public static StatusUpdate TempoChange(float tempo) => new(StatusType.SequenceTempoChange, tempo);

    public static StatusUpdate SequenceLoaded(MidiSequence sequence) => new(StatusType.SequenceLoad, sequence);

    // Network statuses

    public static readonly StatusUpdate NetStatusChanged = new(StatusType.NetStatusChanged, null);
    public static readonly StatusUpdate NetDevicesChanged = new(StatusType.NetDevicesChanged, null);

    public override string ToString() => Data is null ? $"StatusUpdate[{Type}]" : $"StatusUpdate[{Type}: {Data}]";
}
