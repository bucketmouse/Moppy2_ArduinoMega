using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Moppy.Core.Midi;

/// <summary>
/// Plays a <see cref="MidiSequence"/> in real time, sending its messages to <see cref="Receiver"/> and its meta
/// events to <see cref="MetaEventReceived"/>.
/// </summary>
/// <remarks>
/// Behaves like the Java <c>javax.sound.midi.Sequencer</c> the Java version of Moppy used:
/// <list type="bullet">
/// <item>Tempo meta events change the playback tempo as they're played; <see cref="TempoInBpm"/> may also be
/// changed at any time.</item>
/// <item>Changing the position (or sequence) picks up the tempo in effect at the new position.</item>
/// <item>Stopping, or repositioning while running, sends note-offs for any sounding notes (plus All Notes Off /
/// Sustain Off controller messages on every channel).</item>
/// <item>When the end of the sequence is reached playback stops and a single End of Track meta event
/// (type 47) is sent to <see cref="MetaEventReceived"/>.</item>
/// </list>
/// Messages and meta events are delivered on the sequencer's playback thread.  Handlers may call back into the
/// sequencer, but must not block waiting for another thread that is calling into the sequencer.
/// </remarks>
public sealed partial class MidiSequencer : IDisposable
{
    private const double MaxWaitMillis = 20;

    private readonly object sync = new();          // Guards position, tempo, and run state
    private readonly object dispatchLock = new();  // Serializes delivery of messages to the receiver
    private readonly AutoResetEvent wake = new(false);
    private readonly bool[,] soundingNotes = new bool[16, 128]; // Guarded by dispatchLock

    private MidiSequence? sequence;
    private MidiEvent[] events = [];
    private int nextEventIndex;
    private bool running;
    private int generation;
    private double checkpointTick;
    private long checkpointTimestamp;
    private double tempoMpq = MidiSequence.DefaultTempoMpq;
    private volatile IMidiReceiver? receiver;
    private bool disposed;

    private static ILogger Log => MoppyLogging.CreateLogger<MidiSequencer>();

    /// <summary>Raised (on the playback thread) for each meta event played, and once at the end of the sequence.</summary>
    public event Action<MetaMessage>? MetaEventReceived;

    /// <summary>Where played (non-meta) MIDI messages are sent.</summary>
    public IMidiReceiver? Receiver
    {
        get => receiver;
        set => receiver = value;
    }

    /// <summary>
    /// The sequence to play.  Setting it rewinds to the start and resets the tempo to the sequence's initial tempo;
    /// if the sequencer is running it keeps running with the new sequence.  Setting <c>null</c> stops playback.
    /// </summary>
    public MidiSequence? Sequence
    {
        get
        {
            lock (sync)
            {
                return sequence;
            }
        }
        set
        {
            lock (dispatchLock)
            {
                bool wasRunning;
                lock (sync)
                {
                    wasRunning = running;
                    sequence = value;
                    events = value is null ? [] : MergeTracks(value);
                    nextEventIndex = 0;
                    checkpointTick = 0;
                    checkpointTimestamp = Stopwatch.GetTimestamp();
                    tempoMpq = value?.GetTempoMpqAt(0) ?? MidiSequence.DefaultTempoMpq;
                    if (value is null)
                    {
                        running = false;
                        generation++;
                    }
                }

                if (wasRunning)
                {
                    SendNotesOff(value is null);
                }

                wake.Set();
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (sync)
            {
                return running;
            }
        }
    }

    /// <summary>Starts (or resumes) playback from the current position.</summary>
    /// <exception cref="InvalidOperationException">If no sequence is set.</exception>
    public void Start()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sequence is null)
            {
                throw new InvalidOperationException("Sequence not set");
            }

            if (running)
            {
                return;
            }

            running = true;
            int myGeneration = ++generation;
            checkpointTimestamp = Stopwatch.GetTimestamp();
            var thread = new Thread(() => PlayLoop(myGeneration))
            {
                IsBackground = true,
                Name = "Moppy MIDI sequencer",
                Priority = ThreadPriority.AboveNormal,
            };
            thread.Start();
        }
    }

    /// <summary>Stops (pauses) playback at the current position.</summary>
    public void Stop()
    {
        lock (dispatchLock)
        {
            lock (sync)
            {
                if (!running)
                {
                    return;
                }

                checkpointTick = CurrentTick();
                running = false;
                generation++;
            }

            wake.Set();
            SendNotesOff(true);
        }
    }

    public long TickLength => Sequence?.TickLength ?? 0;

    public long MicrosecondLength => Sequence?.MicrosecondLength ?? 0;

    public long TickPosition
    {
        get
        {
            lock (sync)
            {
                return sequence is null ? 0 : (long)Math.Min(CurrentTick(), sequence.TickLength);
            }
        }
        set
        {
            lock (dispatchLock)
            {
                bool wasRunning;
                lock (sync)
                {
                    if (sequence is null)
                    {
                        return;
                    }

                    long tick = Math.Clamp(value, 0, sequence.TickLength);
                    wasRunning = running;
                    checkpointTick = tick;
                    checkpointTimestamp = Stopwatch.GetTimestamp();
                    nextEventIndex = FirstEventIndexAtOrAfter(tick);
                    tempoMpq = sequence.GetTempoMpqAt(tick);
                }

                if (wasRunning)
                {
                    SendNotesOff(false);
                }

                wake.Set();
            }
        }
    }

    /// <summary>Position in microseconds, according to the sequence's tempo map.</summary>
    public long MicrosecondPosition
    {
        get
        {
            var current = Sequence;
            return current?.TickToMicrosecond(TickPosition) ?? 0;
        }
        set
        {
            var current = Sequence;
            if (current is not null)
            {
                TickPosition = current.MicrosecondToTick(value);
            }
        }
    }

    /// <summary>Current playback tempo in microseconds per quarter note.</summary>
    public double TempoInMpq
    {
        get
        {
            lock (sync)
            {
                return tempoMpq;
            }
        }
        set
        {
            if (!(value > 0) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Tempo must be positive");
            }

            lock (sync)
            {
                if (running)
                {
                    // Re-anchor so the new tempo only applies from now on
                    checkpointTick = CurrentTick();
                    checkpointTimestamp = Stopwatch.GetTimestamp();
                }

                tempoMpq = value;
            }

            wake.Set();
        }
    }

    /// <summary>Current playback tempo in beats per minute.</summary>
    public float TempoInBpm
    {
        get => (float)(60_000_000.0 / TempoInMpq);
        set => TempoInMpq = 60_000_000.0 / value;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        Stop();
    }

    private static MidiEvent[] MergeTracks(MidiSequence sequence) =>
        sequence.Tracks
            .SelectMany((track, trackIndex) => track.Events.Select((e, eventIndex) => (e, trackIndex, eventIndex)))
            .Where(x => !MidiTrack.IsEndOfTrack(x.e.Message))
            .OrderBy(x => x.e.Tick).ThenBy(x => x.trackIndex).ThenBy(x => x.eventIndex)
            .Select(x => x.e)
            .ToArray();

    // Must hold sync
    private double TicksPerMicrosecond => sequence!.DivisionType == DivisionType.Ppq
        ? sequence.Resolution / tempoMpq
        : sequence.FramesPerSecond * sequence.Resolution / 1_000_000.0;

    // Must hold sync
    private double CurrentTick()
    {
        if (!running || sequence is null)
        {
            return checkpointTick;
        }

        double elapsedMicros = Stopwatch.GetElapsedTime(checkpointTimestamp).Ticks / 10.0;
        return checkpointTick + elapsedMicros * TicksPerMicrosecond;
    }

    // Must hold sync
    private int FirstEventIndexAtOrAfter(long tick)
    {
        int lo = 0, hi = events.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (events[mid].Tick < tick)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private void PlayLoop(int myGeneration)
    {
        using var timerResolution = TimerResolution.Begin();
        var batch = new List<MidiMessage>();

        while (true)
        {
            bool ended;
            double waitMicros;
            lock (dispatchLock)
            {
                lock (sync)
                {
                    if (!running || generation != myGeneration || sequence is null)
                    {
                        return;
                    }

                    double now = CurrentTick();
                    while (nextEventIndex < events.Length && events[nextEventIndex].Tick <= now)
                    {
                        var e = events[nextEventIndex++];
                        if (e.Message is MetaMessage meta && meta.TryGetTempo(out int mpq) && sequence.DivisionType == DivisionType.Ppq)
                        {
                            // Re-anchor timing at the tempo event so later events are timed with the new tempo
                            double microsToEvent = (e.Tick - checkpointTick) / TicksPerMicrosecond;
                            checkpointTimestamp += (long)(microsToEvent * Stopwatch.Frequency / 1_000_000.0);
                            checkpointTick = e.Tick;
                            tempoMpq = mpq;
                            now = CurrentTick();
                        }

                        batch.Add(e.Message);
                    }

                    ended = nextEventIndex >= events.Length && now >= sequence.TickLength;
                    if (ended)
                    {
                        running = false;
                        checkpointTick = sequence.TickLength;
                    }

                    long nextTick = nextEventIndex < events.Length ? events[nextEventIndex].Tick : sequence.TickLength;
                    waitMicros = (nextTick - now) / TicksPerMicrosecond;
                }

                foreach (var message in batch)
                {
                    if (Volatile.Read(ref generation) != myGeneration)
                    {
                        break; // Stopped or restarted by a handler
                    }

                    Dispatch(message);
                }

                batch.Clear();

                if (ended)
                {
                    SendNotesOff(true);
                    RaiseMetaEvent(MetaMessage.EndOfTrack);
                    return;
                }
            }

            if (waitMicros > 1500)
            {
                wake.WaitOne(TimeSpan.FromMilliseconds(Math.Min((waitMicros - 1000) / 1000.0, MaxWaitMillis)));
            }
            else
            {
                Thread.Yield();
            }
        }
    }

    // Must hold dispatchLock
    private void Dispatch(MidiMessage message)
    {
        if (message is MetaMessage meta)
        {
            RaiseMetaEvent(meta);
            return;
        }

        if (message is ShortMessage { Command: ShortMessage.NoteOn or ShortMessage.NoteOff } note)
        {
            soundingNotes[note.Channel, note.Data1] = note.Command == ShortMessage.NoteOn && note.Data2 > 0;
        }

        SendToReceiver(message);
    }

    // Must hold dispatchLock
    private void SendNotesOff(bool doControllers)
    {
        for (int channel = 0; channel < 16; channel++)
        {
            for (int note = 0; note < 128; note++)
            {
                if (soundingNotes[channel, note])
                {
                    soundingNotes[channel, note] = false;
                    SendToReceiver(new ShortMessage(ShortMessage.NoteOn, channel, note, 0));
                }
            }

            SendToReceiver(new ShortMessage(ShortMessage.ControlChange, channel, 123, 0)); // All notes off
            SendToReceiver(new ShortMessage(ShortMessage.ControlChange, channel, 64, 0));  // Sustain off
            if (doControllers)
            {
                SendToReceiver(new ShortMessage(ShortMessage.ControlChange, channel, 121, 0)); // Reset all controllers
            }
        }
    }

    private void SendToReceiver(MidiMessage message)
    {
        try
        {
            receiver?.Send(message, -1);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "MIDI receiver failed handling {Message}", message);
        }
    }

    private void RaiseMetaEvent(MetaMessage meta)
    {
        try
        {
            MetaEventReceived?.Invoke(meta);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Meta event handler failed handling {Message}", meta);
        }
    }

    /// <summary>Requests 1ms timer resolution on Windows while playing, so short waits are accurate.</summary>
    private sealed partial class TimerResolution : IDisposable
    {
        private TimerResolution()
        {
        }

        public static IDisposable? Begin()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                timeBeginPeriod(1);
                return new TimerResolution();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        public void Dispose() => timeEndPeriod(1);

        [LibraryImport("winmm.dll")]
        private static partial uint timeBeginPeriod(uint period);

        [LibraryImport("winmm.dll")]
        private static partial uint timeEndPeriod(uint period);
    }
}
