using Moppy.Core.Midi;

namespace Moppy.Core.Tests;

public class MidiFileTests
{
    /// <summary>Builds a format 1 SMF from raw track bodies.</summary>
    internal static byte[] BuildFile(int division, params byte[][] tracks)
    {
        var bytes = new List<byte>();
        bytes.AddRange("MThd"u8.ToArray());
        bytes.AddRange([0, 0, 0, 6, 0, 1, 0, (byte)tracks.Length, (byte)(division >> 8), (byte)division]);
        foreach (var track in tracks)
        {
            bytes.AddRange("MTrk"u8.ToArray());
            bytes.AddRange([(byte)(track.Length >> 24), (byte)(track.Length >> 16), (byte)(track.Length >> 8), (byte)track.Length]);
            bytes.AddRange(track);
        }

        return bytes.ToArray();
    }

    [Fact]
    public void ReadsEventsWithRunningStatusSysexAndMeta()
    {
        byte[] track =
        [
            0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20, // Tempo 500000 at 0
            0x00, 0x90, 60, 100,                      // Note on
            0x60, 62, 100,                            // Running status note on at 96
            0x00, 0xF0, 0x04, 0x4d, 0x01, 0x02, 0xF7, // Sysex
            0x81, 0x40, 0x80, 60, 0,                  // Note off at 96 + 192
            0x00, 0xC1, 5,                            // Program change (1 data byte)
            0x00, 0xFF, 0x2F, 0x00,                   // End of track
        ];
        var sequence = MidiFileReader.Read(BuildFile(96, track));

        Assert.Equal(DivisionType.Ppq, sequence.DivisionType);
        Assert.Equal(96, sequence.Resolution);
        var events = sequence.Tracks.Single().Events;
        Assert.Equal([0L, 0, 96, 96, 288, 288, 288], events.Select(e => e.Tick));
        Assert.IsType<MetaMessage>(events[0].Message);
        Assert.Equal([0x90, 62, 100], events[2].Message.ToArray());
        Assert.Equal([0x4d, 0x01, 0x02, 0xF7], ((SysexMessage)events[3].Message).Data.ToArray());
        Assert.Equal([0xC1, 5], events[5].Message.ToArray());
        Assert.Equal(288, sequence.TickLength);
    }

    [Fact]
    public void ToleratesTruncatedTracksAndMissingEndOfTrack()
    {
        byte[] track = [0x00, 0x90, 60, 100, 0x10, 0x80, 60, 0, 0x20, 0x90, 61]; // Last event truncated
        var file = BuildFile(480, track);
        file[^(track.Length + 1)] = 0xFF; // Claim the track is longer than the file
        var sequence = MidiFileReader.Read(file);

        var events = sequence.Tracks.Single().Events;
        Assert.Equal(3, events.Count); // Two notes plus an added End of Track
        Assert.Equal(16, sequence.TickLength);
    }

    [Fact]
    public void RejectsNonMidiData()
    {
        Assert.Throws<InvalidMidiDataException>(() => MidiFileReader.Read("RIFF1234WAVEfmt "u8));
        Assert.Throws<InvalidMidiDataException>(() => MidiFileReader.Read([]));
    }

    [Fact]
    public void TempoMapConvertsBetweenTicksAndMicroseconds()
    {
        var sequence = new MidiSequence(DivisionType.Ppq, 100,
        [
            new MidiTrack(
            [
                new MidiEvent(0, MetaMessage.Tempo(500_000)),   // 120 BPM
                new MidiEvent(200, MetaMessage.Tempo(250_000)), // 240 BPM from tick 200
                new MidiEvent(400, TestHelpers.NoteOff(0, 60)),
            ]),
        ]);

        Assert.Equal(1_000_000, sequence.TickToMicrosecond(200));
        Assert.Equal(1_500_000, sequence.TickToMicrosecond(400));
        Assert.Equal(1_500_000, sequence.MicrosecondLength);
        Assert.Equal(300, sequence.MicrosecondToTick(1_250_000));
        Assert.Equal(100, sequence.MicrosecondToTick(500_000));
        Assert.Equal(500_000, sequence.GetTempoMpqAt(199));
        Assert.Equal(250_000, sequence.GetTempoMpqAt(200));
    }

    [Theory]
    [InlineData("KirbysTheme.mid")]
    [InlineData("O Cara Mia, Addio (Turret Opera).mid")]
    [InlineData("Tetris.mid")]
    public void ReadsSampleSongs(string name)
    {
        var sequence = MidiFileReader.Read(TestHelpers.SampleSong(name));
        Assert.NotEmpty(sequence.Tracks);
        Assert.True(sequence.MicrosecondLength > 10_000_000);
        Assert.Contains(sequence.Tracks.SelectMany(t => t.Events), e => e.Message is ShortMessage { Command: ShortMessage.NoteOn });
    }
}
