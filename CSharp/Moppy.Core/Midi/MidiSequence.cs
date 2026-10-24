namespace Moppy.Core.Midi;

/// <summary>Timing division type of a sequence.</summary>
public enum DivisionType
{
    /// <summary>Tempo-based timing; resolution is ticks per quarter note.</summary>
    Ppq,
    /// <summary>SMPTE 24 frames per second; resolution is ticks per frame.</summary>
    Smpte24,
    /// <summary>SMPTE 25 frames per second; resolution is ticks per frame.</summary>
    Smpte25,
    /// <summary>SMPTE 29.97 (drop frame) frames per second; resolution is ticks per frame.</summary>
    Smpte30Drop,
    /// <summary>SMPTE 30 frames per second; resolution is ticks per frame.</summary>
    Smpte30,
}

/// <summary>A MIDI message at a particular tick of a track.</summary>
public readonly record struct MidiEvent(long Tick, MidiMessage Message);

/// <summary>
/// A track of time-ordered MIDI events.  The last event is always an End of Track meta event.
/// </summary>
public sealed class MidiTrack
{
    /// <summary>Creates a track; events are sorted by tick (stably) and an End of Track event is added if missing.</summary>
    public MidiTrack(IEnumerable<MidiEvent> events)
    {
        var all = events.ToList();
        var sorted = all
            .Where(e => !IsEndOfTrack(e.Message))
            .OrderBy(e => e.Tick)
            .ToList();
        long endTick = all.Select(e => e.Tick).DefaultIfEmpty(0).Max();
        sorted.Add(new MidiEvent(endTick, MetaMessage.EndOfTrack));
        Events = sorted;
    }

    public IReadOnlyList<MidiEvent> Events { get; }

    /// <summary>Tick of the End of Track event.</summary>
    public long TickLength => Events[^1].Tick;

    internal static bool IsEndOfTrack(MidiMessage message) =>
        message is MetaMessage { Type: MetaMessage.EndOfTrackType };
}

/// <summary>
/// A MIDI sequence (e.g. loaded from a MIDI file): a set of tracks plus timing information.
/// </summary>
public sealed class MidiSequence
{
    /// <summary>Default tempo when a sequence doesn't specify one: 120 BPM.</summary>
    public const int DefaultTempoMpq = 500_000;

    private readonly (long Tick, int Mpq)[] tempoChanges;
    private readonly long[] tempoChangeMicros;

    public MidiSequence(DivisionType divisionType, int resolution, IEnumerable<MidiTrack> tracks)
    {
        if (resolution <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Resolution must be positive");
        }

        DivisionType = divisionType;
        Resolution = resolution;
        Tracks = tracks.ToList();
        TickLength = Tracks.Select(t => t.TickLength).DefaultIfEmpty(0).Max();

        // Build the tempo map from tempo events on every track (ordered by tick, then track)
        var tempoEvents = new List<(long Tick, int Track, int Mpq)>();
        for (int t = 0; t < Tracks.Count; t++)
        {
            foreach (var e in Tracks[t].Events)
            {
                if (e.Message is MetaMessage meta && meta.TryGetTempo(out int mpq))
                {
                    tempoEvents.Add((e.Tick, t, mpq));
                }
            }
        }

        var changes = new List<(long Tick, int Mpq)> { (0, DefaultTempoMpq) };
        foreach (var (tick, _, mpq) in tempoEvents.OrderBy(e => e.Tick).ThenBy(e => e.Track))
        {
            if (changes[^1].Tick == tick)
            {
                changes[^1] = (tick, mpq); // Last tempo at a given tick wins
            }
            else
            {
                changes.Add((tick, mpq));
            }
        }

        tempoChanges = changes.ToArray();
        tempoChangeMicros = new long[tempoChanges.Length];
        for (int i = 1; i < tempoChanges.Length; i++)
        {
            tempoChangeMicros[i] = tempoChangeMicros[i - 1] +
                TicksToMicros(tempoChanges[i].Tick - tempoChanges[i - 1].Tick, tempoChanges[i - 1].Mpq);
        }

        MicrosecondLength = TickToMicrosecond(TickLength);
    }

    public DivisionType DivisionType { get; }

    /// <summary>Ticks per quarter note (PPQ) or ticks per frame (SMPTE).</summary>
    public int Resolution { get; }

    public IReadOnlyList<MidiTrack> Tracks { get; }

    /// <summary>Length of the sequence in ticks (the latest End of Track).</summary>
    public long TickLength { get; }

    /// <summary>Length of the sequence in microseconds, according to the sequence's own tempo map.</summary>
    public long MicrosecondLength { get; }

    /// <summary>Frames per second for SMPTE sequences (0 for PPQ).</summary>
    public double FramesPerSecond => DivisionType switch
    {
        DivisionType.Smpte24 => 24,
        DivisionType.Smpte25 => 25,
        DivisionType.Smpte30Drop => 29.97,
        DivisionType.Smpte30 => 30,
        _ => 0,
    };

    /// <summary>The tempo (microseconds per quarter note) in effect at the given tick.</summary>
    public int GetTempoMpqAt(long tick) => tempoChanges[TempoIndexAtTick(tick)].Mpq;

    /// <summary>Converts a tick position to microseconds using the sequence's tempo map.</summary>
    public long TickToMicrosecond(long tick)
    {
        if (DivisionType != DivisionType.Ppq)
        {
            return (long)(tick * 1_000_000.0 / (FramesPerSecond * Resolution));
        }

        int i = TempoIndexAtTick(tick);
        return tempoChangeMicros[i] + TicksToMicros(tick - tempoChanges[i].Tick, tempoChanges[i].Mpq);
    }

    /// <summary>Converts microseconds to a tick position using the sequence's tempo map.</summary>
    public long MicrosecondToTick(long microseconds)
    {
        if (DivisionType != DivisionType.Ppq)
        {
            return (long)(microseconds * FramesPerSecond * Resolution / 1_000_000.0);
        }

        int i = Array.BinarySearch(tempoChangeMicros, microseconds);
        if (i < 0)
        {
            i = ~i - 1;
        }

        i = Math.Clamp(i, 0, tempoChanges.Length - 1);
        return tempoChanges[i].Tick + (long)((microseconds - tempoChangeMicros[i]) * (double)Resolution / tempoChanges[i].Mpq);
    }

    private long TicksToMicros(long ticks, int mpq) => (long)((double)ticks * mpq / Resolution);

    private int TempoIndexAtTick(long tick)
    {
        // Last tempo change at or before tick
        int lo = 0, hi = tempoChanges.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (tempoChanges[mid].Tick <= tick)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }
}
