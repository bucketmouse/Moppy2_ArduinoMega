using Moppy.Core.Comms;
using Moppy.Core.Control;
using Moppy.Core.Events.Mapper;
using Moppy.Core.Events.Mapper.Scripts;
using Moppy.Core.Midi;
using static Moppy.Core.Tests.TestHelpers;

namespace Moppy.Core.Tests;

public class MapperTests
{
    [Fact]
    public void DefaultScriptsMapChannelToSubAddress()
    {
        var mapper = new MidiScriptMapper();
        Assert.Equal(MoppyMessageFactory.DevicePlayNote(1, 3, 60, 100), mapper.MapEvent(NoteOn(2, 60, 100)));
        Assert.Equal(MoppyMessageFactory.DeviceStopNote(1, 3, 60), mapper.MapEvent(NoteOff(2, 60)));
        Assert.Equal(MoppyMessageFactory.DeviceStopNote(1, 3, 60), mapper.MapEvent(NoteOn(2, 60, 0)));
        Assert.Equal(MoppyMessageFactory.DevicePitchBend(1, 3, 0), mapper.MapEvent(new ShortMessage(ShortMessage.PitchBend, 2, 0, 64)));
        Assert.Equal(MoppyMessageFactory.DevicePitchBend(1, 3, 8191), mapper.MapEvent(new ShortMessage(ShortMessage.PitchBend, 2, 127, 127)));
        Assert.Null(mapper.MapEvent(new ShortMessage(ShortMessage.ControlChange, 2, 7, 100)));
    }

    [Fact]
    public void DefaultMapperMatchesScriptMapperDefaults()
    {
        var defaultMapper = MidiEventMapper.DefaultMapper(1);
        var scriptMapper = new MidiScriptMapper();
        foreach (var message in new MidiMessage[]
                 {
                     NoteOn(0, 40, 99), NoteOff(15, 40), NoteOn(4, 40, 0),
                     new ShortMessage(ShortMessage.PitchBend, 0, 0x7F, 0x40), new ShortMessage(ShortMessage.PitchBend, 0, 0x01, 0x00),
                 })
        {
            Assert.Equal(scriptMapper.MapEvent(message), defaultMapper.MapEvent(message));
        }
    }

    [Fact]
    public void PresetScriptsWork()
    {
        var mapper = new MidiScriptMapper
        {
            ConditionScript = ConditionScripts.OnlySupportedNotes.Script,
            DeviceAddressScript = DeviceAddressScripts.OneDevicePerChannel.Script,
            NoteScript = NoteScripts.ForceIntoRange.Script,
        };

        Assert.Null(mapper.MapEvent(NoteOn(0, 22, 100))); // Unsupported note
        Assert.Equal(MoppyMessageFactory.DevicePlayNote(4, 4, 24 + (60 - 24) % 48, 100), mapper.MapEvent(NoteOn(3, 60, 100)));
    }

    [Fact]
    public void RoundRobinRotatesNotesAndStopsTheRightDrive()
    {
        var mapper = new MidiScriptMapper { SubAddressScript = "nextRoundRobinSubAddress(3)" };
        byte SubAddressOf(MidiMessage m) => mapper.MapEvent(m)!.SubAddress;

        Assert.Equal(1, SubAddressOf(NoteOn(0, 60, 100)));
        Assert.Equal(2, SubAddressOf(NoteOn(0, 62, 100)));
        Assert.Equal(3, SubAddressOf(NoteOn(0, 64, 100)));
        Assert.Equal(1, SubAddressOf(NoteOn(0, 65, 100))); // Wraps around (replacing 60 on drive 1)
        Assert.Equal(2, SubAddressOf(NoteOff(0, 62)));
        Assert.Equal(3, SubAddressOf(NoteOn(0, 64, 0))); // Zero-velocity note-on is a note-off
        Assert.Equal(4, SubAddressOf(NoteOff(0, 60)));   // Not playing anywhere any more: out of range
        Assert.Equal(4, SubAddressOf(new ShortMessage(ShortMessage.PitchBend, 0, 0, 64)));
    }

    [Fact]
    public void ValidatingAScriptDoesNotDisturbRoundRobinState()
    {
        var mapper = new MidiScriptMapper { SubAddressScript = SubAddressScripts.RoundRobin.Script };
        Assert.Equal(1, mapper.MapEvent(NoteOn(0, 60, 100))!.SubAddress);
        mapper.SubAddressScript = SubAddressScripts.RoundRobin.Script; // Re-validates (evaluating the script)
        Assert.Equal(2, mapper.MapEvent(NoteOn(0, 62, 100))!.SubAddress);
        Assert.Equal(1, mapper.MapEvent(NoteOff(0, 60))!.SubAddress);
    }

    [Theory]
    [InlineData("n +")]               // Syntax error
    [InlineData("1")]                 // Not a boolean
    [InlineData("undefinedVariable")] // Throws
    [InlineData("while(true){}")]     // Never finishes
    public void InvalidConditionScriptsAreRejected(string script)
    {
        var mapper = new MidiScriptMapper();
        Assert.Throws<MapperScriptException>(() => mapper.ConditionScript = script);
        Assert.Equal("true", mapper.ConditionScript); // Unchanged
    }

    [Theory]
    [InlineData("n / 2")]    // Not a whole number for odd notes
    [InlineData("n * 3")]    // Out of byte range for high notes
    [InlineData("'one'")]    // Not a number
    public void InvalidNumericScriptsAreRejected(string script)
    {
        var mapper = new MidiScriptMapper();
        Assert.Throws<MapperScriptException>(() => mapper.NoteScript = script);
        Assert.Equal("n", mapper.NoteScript);
    }

    [Fact]
    public void ScriptsThatFailForAnEventAreSkipped()
    {
        var mapper = new MidiScriptMapper { DeviceAddressScript = "c < 15 ? 1 : 2" };
        mapper.NoteScript = "n < 127 ? n : 0";
        Assert.NotNull(mapper.MapEvent(NoteOn(0, 60, 100)));

        // Passes validation but returns a non-number for a particular input
        mapper.NoteScript = "n == 61 ? 'oops' : n";
        Assert.Null(mapper.MapEvent(NoteOn(0, 61, 100)));
        Assert.NotNull(mapper.MapEvent(NoteOn(0, 62, 100)));
    }

    [Fact]
    public void MoppySysexMessagesPassStraightThrough()
    {
        var mapper = new MidiScriptMapper();
        byte[] moppyBytes = [0x4d, 0x02, 0x01, 0x02, 0x61, 0x10];
        var sysex = new SysexMessage(SysexMessage.SystemExclusive, [.. moppyBytes, 0xF7]);
        Assert.Equal(MoppyMessageFactory.FromBytes(moppyBytes), mapper.MapEvent(sysex));
        Assert.Null(mapper.MapEvent(new SysexMessage(SysexMessage.SystemExclusive, [0x7E, 0x00, 0xF7])));
    }

    [Fact]
    public void MapperCollectionMapsWithEveryMapperInOrder()
    {
        var collection = new MapperCollection<MidiMessage>();
        var first = new MidiScriptMapper();
        var second = new MidiScriptMapper { DeviceAddressScript = "2", ConditionScript = "n > 50" };
        collection.AddMapper(first);
        collection.AddMapper(second);

        Assert.Equal([1, 2], collection.MapEvent(NoteOn(0, 60, 100)).Select(m => (int)m.DeviceAddress));
        Assert.Equal([1], collection.MapEvent(NoteOn(0, 40, 100)).Select(m => (int)m.DeviceAddress));
        collection.RemoveMapper(first);
        Assert.Equal([2], collection.MapEvent(NoteOn(0, 60, 100)).Select(m => (int)m.DeviceAddress));
    }

    [Fact]
    public void ConfiguredMapperAppliesPresetsAndCustomScripts()
    {
        var config = new MidiScriptMapperConfig();
        var configured = new ConfiguredMapper(config);
        Assert.Equal("All Events", configured.GetChoice(MapperField.Condition));
        Assert.Equal(["All Events", "Channels 1-4", "Only Supported Notes", "Custom"], MapperPresets.ChoicesFor(MapperField.Condition));

        Assert.True(configured.SelectChoice(MapperField.SubAddress, SubAddressScripts.RoundRobin.DisplayName));
        Assert.Equal("nextRoundRobinSubAddress(8)", configured.Mapper.SubAddressScript);
        Assert.Equal(SubAddressScripts.RoundRobin.DisplayName, config.SubAddressChoice);

        // Custom uses the saved custom script
        Assert.True(configured.SelectChoice(MapperField.Note, MapperPresets.CustomChoice));
        Assert.Equal("n", configured.Mapper.NoteScript);
        Assert.True(configured.SetCustomScript(MapperField.Note, "n+12"));
        Assert.Equal("n+12", config.NoteCustomScript);
        Assert.Null(configured.GetError(MapperField.Note));

        // An invalid custom script is reported and not saved
        Assert.False(configured.SetCustomScript(MapperField.Note, "n +"));
        Assert.NotNull(configured.GetError(MapperField.Note));
        Assert.Equal("n+12", config.NoteCustomScript);
        Assert.Equal("n+12", configured.Mapper.NoteScript);

        // Switching back to a preset keeps the custom script for later
        Assert.True(configured.SelectChoice(MapperField.Note, NoteScripts.StraightThrough.DisplayName));
        Assert.Null(configured.GetError(MapperField.Note));
        Assert.Equal("n+12", configured.GetCustomScript(MapperField.Note));
        Assert.Throws<InvalidOperationException>(() => configured.SetCustomScript(MapperField.Note, "n"));
    }

    [Fact]
    public void ConfiguredMapperRestoresSavedConfig()
    {
        var config = new MidiScriptMapperConfig
        {
            ConditionChoice = "Custom",
            ConditionCustomScript = "c == 9",
            DeviceAddressChoice = "No longer a preset",
            NoteChoice = "Custom",
            NoteCustomScript = "this is not javascript",
        };
        var configured = new ConfiguredMapper(config);

        Assert.Equal("c == 9", configured.Mapper.ConditionScript);
        Assert.Equal(DeviceAddressScripts.DeviceOne.DisplayName, configured.GetChoice(MapperField.DeviceAddress));
        Assert.NotNull(configured.GetError(MapperField.Note));
        Assert.Equal("n", configured.Mapper.NoteScript);
    }
}
