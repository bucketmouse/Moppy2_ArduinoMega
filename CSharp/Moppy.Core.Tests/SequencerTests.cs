using Moppy.Core.Midi;
using static Moppy.Core.Tests.TestHelpers;

namespace Moppy.Core.Tests;

public class SequencerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void PlaysEventsInOrderAtTheRightTime()
    {
        // 120 BPM, 100 ticks per quarter: 1 tick = 5ms
        var sequence = Sequence(100,
            new MidiEvent(0, NoteOn(0, 60, 100)),
            new MidiEvent(20, NoteOff(0, 60)),  // 100ms
            new MidiEvent(40, NoteOn(1, 62, 100)), // 200ms
            new MidiEvent(60, NoteOff(1, 62)));    // 300ms
        var receiver = new RecordingReceiver();
        using var sequencer = new MidiSequencer { Receiver = receiver, Sequence = sequence };
        var ended = new ManualResetEventSlim();
        int endCount = 0;
        sequencer.MetaEventReceived += meta =>
        {
            if (meta.Type == MetaMessage.EndOfTrackType)
            {
                Interlocked.Increment(ref endCount);
                ended.Set();
            }
        };

        sequencer.Start();
        Assert.True(ended.Wait(Timeout));
        Assert.False(sequencer.IsRunning);
        Assert.Equal(1, endCount);

        var notes = receiver.Received.Where(r => r.Message is ShortMessage { Command: not ShortMessage.ControlChange }).ToArray();
        Assert.Equal(4, notes.Length);
        Assert.Equal([0x90, 0x80, 0x91, 0x81], notes.Select(n => n.Message.Status));
        var start = notes[0].Time;
        AssertTiming(TimeSpan.FromMilliseconds(100), notes[1].Time - start);
        AssertTiming(TimeSpan.FromMilliseconds(200), notes[2].Time - start);
        AssertTiming(TimeSpan.FromMilliseconds(300), notes[3].Time - start);
    }

    [Fact]
    public void TempoEventsChangePlaybackSpeed()
    {
        var sequence = Sequence(100,
            new MidiEvent(0, MetaMessage.Tempo(1_000_000)),  // 60 BPM: 10ms per tick
            new MidiEvent(10, NoteOn(0, 60, 100)),           // 100ms
            new MidiEvent(10, MetaMessage.Tempo(250_000)),   // 240 BPM: 2.5ms per tick
            new MidiEvent(50, NoteOff(0, 60)));              // 100ms + 100ms
        var receiver = new RecordingReceiver();
        using var sequencer = new MidiSequencer { Receiver = receiver, Sequence = sequence };
        Assert.Equal(60f, sequencer.TempoInBpm, 3);
        var tempos = new List<int>();
        var ended = new ManualResetEventSlim();
        sequencer.MetaEventReceived += meta =>
        {
            if (meta.TryGetTempo(out int mpq))
            {
                tempos.Add(mpq);
            }
            else if (meta.Type == MetaMessage.EndOfTrackType)
            {
                ended.Set();
            }
        };

        var started = DateTime.UtcNow;
        sequencer.Start();
        Assert.True(ended.Wait(Timeout));
        var notes = receiver.Notes.Take(2).ToArray();
        Assert.Equal([1_000_000, 250_000], tempos);
        Assert.Equal(240f, sequencer.TempoInBpm, 3);
        var times = receiver.Received.Where(r => r.Message is ShortMessage { Command: ShortMessage.NoteOn or ShortMessage.NoteOff }).Select(r => r.Time).ToArray();
        AssertTiming(TimeSpan.FromMilliseconds(100), times[1] - times[0]);
    }

    [Fact]
    public void ChangingTempoWhilePlayingAffectsRemainingEvents()
    {
        var sequence = Sequence(100,
            new MidiEvent(0, NoteOn(0, 60, 100)),
            new MidiEvent(100, NoteOff(0, 60))); // 500ms at 120 BPM
        var receiver = new RecordingReceiver();
        using var sequencer = new MidiSequencer { Receiver = receiver, Sequence = sequence };
        var ended = new ManualResetEventSlim();
        sequencer.MetaEventReceived += meta => ended.Set();

        sequencer.Start();
        Thread.Sleep(100);              // 20 ticks played
        sequencer.TempoInBpm = 480;     // Remaining 80 ticks at 1.25ms per tick = 100ms
        Assert.True(ended.Wait(Timeout));
        var times = receiver.Received.Where(r => r.Message is ShortMessage { Command: ShortMessage.NoteOn or ShortMessage.NoteOff }).Select(r => r.Time).ToArray();
        AssertTiming(TimeSpan.FromMilliseconds(200), times[1] - times[0], toleranceMs: 60);
    }

    [Fact]
    public void StoppingTurnsOffSoundingNotesAndKeepsPosition()
    {
        var sequence = Sequence(100,
            new MidiEvent(0, NoteOn(0, 60, 100)),
            new MidiEvent(0, NoteOn(3, 64, 100)),
            new MidiEvent(1000, NoteOff(0, 60)));
        var receiver = new RecordingReceiver();
        using var sequencer = new MidiSequencer { Receiver = receiver, Sequence = sequence };

        sequencer.Start();
        Thread.Sleep(150);
        sequencer.Stop();
        long position = sequencer.TickPosition;
        Assert.InRange(position, 20, 60);

        var notes = receiver.Notes;
        Assert.Equal(4, notes.Count);
        Assert.Equal([(0, 0, 60), (3, 0, 64)], notes.Skip(2).Select(n => (n.Channel, n.Data2, n.Data1)).OrderBy(n => n.Channel));

        Thread.Sleep(100);
        Assert.Equal(position, sequencer.TickPosition);
        Assert.Equal(4, receiver.Notes.Count); // Nothing more played
    }

    [Fact]
    public void RepositioningSkipsToTheNewPositionAndChasesTempo()
    {
        var sequence = Sequence(100,
            new MidiEvent(0, NoteOn(0, 60, 100)),
            new MidiEvent(100, MetaMessage.Tempo(250_000)),
            new MidiEvent(200, NoteOn(0, 62, 100)),
            new MidiEvent(220, NoteOff(0, 62)));
        var receiver = new RecordingReceiver();
        using var sequencer = new MidiSequencer { Receiver = receiver, Sequence = sequence };
        var ended = new ManualResetEventSlim();
        sequencer.MetaEventReceived += meta =>
        {
            if (meta.Type == MetaMessage.EndOfTrackType)
            {
                ended.Set();
            }
        };

        sequencer.TickPosition = 150;
        Assert.Equal(240f, sequencer.TempoInBpm, 3);
        Assert.Equal(1_000_000 / 2 + 50 * 2_500, sequencer.MicrosecondPosition);
        sequencer.Start();
        Assert.True(ended.Wait(Timeout));
        Assert.Equal([62, 62], receiver.Notes.Select(n => n.Data1));
    }

    [Fact]
    public void StartWithoutSequenceThrows()
    {
        using var sequencer = new MidiSequencer();
        Assert.Throws<InvalidOperationException>(sequencer.Start);
    }

    internal static void AssertTiming(TimeSpan expected, TimeSpan actual, double toleranceMs = 40) =>
        Assert.InRange(actual.TotalMilliseconds, expected.TotalMilliseconds - toleranceMs, expected.TotalMilliseconds + toleranceMs);
}
