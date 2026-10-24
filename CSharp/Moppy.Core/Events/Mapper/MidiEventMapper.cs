using Microsoft.Extensions.Logging;
using Moppy.Core.Comms;
using Moppy.Core.Midi;

namespace Moppy.Core.Events.Mapper;

/// <summary>
/// Maps MIDI events to MoppyMessages.
/// </summary>
public abstract class MidiEventMapper : IEventMapper<MidiMessage>
{
    public abstract MoppyMessage? MapEvent(MidiMessage inputEvent);

    /// <summary>
    /// Maps MIDI channels 1-16 to sub-addresses 1-16 for the given deviceAddress.
    /// </summary>
    public static MidiEventMapper DefaultMapper(byte deviceAddress) => new DefaultMidiEventMapper(deviceAddress);

    /// <summary>
    /// Converts the MIDI pitch bend data bytes (two 7-bit values) into a signed value where 0 = no bend, with a
    /// range of -8192 to 8191.
    /// </summary>
    public static short PitchBendAmount(ShortMessage message) =>
        (short)(((message.Data2 << 7) | message.Data1) - 8192);

    /// <summary>
    /// If the sysex message is a "Moppy" System Exclusive message (data begins with the Moppy start byte), converts it
    /// directly into a MoppyMessage.  NO VALIDATION IS DONE (so the sysex messages must be well-formatted).
    /// </summary>
    protected static MoppyMessage? MapSysex(SysexMessage sysexMessage)
    {
        var data = sysexMessage.Data;
        if (data.IsEmpty || data[0] != MoppyMessage.StartByte)
        {
            return null;
        }

        if (data[^1] == 0xF7)
        {
            data = data[..^1]; // Remove trailing 0xF7 byte
        }

        try
        {
            return MoppyMessageFactory.FromBytes(data);
        }
        catch (ArgumentException ex)
        {
            MoppyLogging.CreateLogger<MidiEventMapper>().LogWarning(ex, "Ignoring malformed Moppy sysex message {Message}", sysexMessage);
            return null;
        }
    }

    private sealed class DefaultMidiEventMapper(byte targetAddress) : MidiEventMapper
    {
        public override MoppyMessage? MapEvent(MidiMessage inputEvent)
        {
            switch (inputEvent)
            {
                case ShortMessage midiMessage:
                    byte subAddress = (byte)(midiMessage.Channel + 1);
                    switch (midiMessage.Command)
                    {
                        case ShortMessage.NoteOn when midiMessage.Data2 == 0:
                            // For zero-velocity notes, turn the note off
                            return MoppyMessageFactory.DeviceStopNote(targetAddress, subAddress, (byte)midiMessage.Data1);
                        case ShortMessage.NoteOn:
                            return MoppyMessageFactory.DevicePlayNote(targetAddress, subAddress, (byte)midiMessage.Data1, (byte)midiMessage.Data2);
                        case ShortMessage.NoteOff:
                            return MoppyMessageFactory.DeviceStopNote(targetAddress, subAddress, (byte)midiMessage.Data1);
                        case ShortMessage.PitchBend:
                            return MoppyMessageFactory.DevicePitchBend(targetAddress, subAddress, PitchBendAmount(midiMessage));
                    }

                    break;
                case SysexMessage sysexMessage:
                    return MapSysex(sysexMessage);
            }

            return null; // We don't know how to handle this event
        }
    }
}
