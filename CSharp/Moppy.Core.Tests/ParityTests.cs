using Moppy.Core.Events.Mapper;
using Moppy.Core.Midi;

namespace Moppy.Core.Tests;

/// <summary>
/// Compares against output recorded from the original Java implementation (see TestData/JavaReference/README.md).
/// </summary>
public class ParityTests
{
    [Theory]
    [InlineData("KirbysTheme.mid", "KirbysTheme.txt")]
    [InlineData("O Cara Mia, Addio (Turret Opera).mid", "OCaraMiaAddioTurretOpera.txt")]
    [InlineData("Tetris.mid", "Tetris.txt")]
    public void MatchesJavaFileParsingTimingAndMapping(string song, string reference)
    {
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", "JavaReference", reference));
        var sequence = MidiFileReader.Read(TestHelpers.SampleSong(song));
        var mapper = MidiEventMapper.DefaultMapper(1);

        var actual = new List<string>
        {
            $"division=0.0 resolution={sequence.Resolution} tracks={sequence.Tracks.Count} ticks={sequence.TickLength} micros={sequence.MicrosecondLength}",
        };
        for (int t = 0; t < sequence.Tracks.Count; t++)
        {
            foreach (var e in sequence.Tracks[t].Events)
            {
                string mapped = e.Message is MetaMessage ? "" : Convert.ToHexString(mapper.MapEvent(e.Message)?.ToArray() ?? []);
                actual.Add($"{t} {e.Tick} {sequence.TickToMicrosecond(e.Tick)} {Convert.ToHexString(e.Message.ToArray())} -> {mapped}");
            }
        }

        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            string[] javaFields = expected[i].Split(' ');
            if (javaFields.Length == 6 && javaFields[3].StartsWith('E'))
            {
                // Pitch bend: Java's defaultMapper computes the bend wrongly; check against MIDIScriptMapper's formula
                Assert.Equal(string.Join(' ', javaFields[..5]), string.Join(' ', actual[i].Split(' ')[..5]));
                byte[] raw = Convert.FromHexString(javaFields[3]);
                int bend = ((raw[2] << 7) + raw[1]) - 8192;
                Assert.EndsWith($"{(bend >> 8) & 0xFF:X2}{bend & 0xFF:X2}", actual[i]);
            }
            else
            {
                Assert.Equal(expected[i], actual[i]);
            }
        }
    }
}
