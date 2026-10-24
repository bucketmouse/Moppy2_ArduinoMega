namespace Moppy.Core.Midi;

/// <summary>
/// Receives MIDI messages (e.g. from a sequencer or a MIDI In device).
/// </summary>
public interface IMidiReceiver
{
    /// <param name="message">The message.</param>
    /// <param name="timeStamp">Time stamp in microseconds, or -1 if not time-stamped.</param>
    void Send(MidiMessage message, long timeStamp);
}

/// <summary>
/// Base class for MIDI messages.  <see cref="Message"/> is the complete message including the status byte.
/// </summary>
public abstract class MidiMessage
{
    private protected readonly byte[] bytes;

    private protected MidiMessage(byte[] bytes)
    {
        this.bytes = bytes;
    }

    /// <summary>The complete message, including the status byte.</summary>
    public ReadOnlySpan<byte> Message => bytes;

    public int Length => bytes.Length;

    /// <summary>The status byte (0 if the message is empty).</summary>
    public int Status => bytes.Length > 0 ? bytes[0] : 0;

    public byte[] ToArray() => (byte[])bytes.Clone();

    /// <summary>
    /// Parses a message as it would appear on a MIDI wire (i.e. 0xFF is a System Reset, not a meta event).
    /// </summary>
    /// <exception cref="InvalidMidiDataException">If the bytes aren't a valid MIDI message.</exception>
    public static MidiMessage FromBytes(ReadOnlySpan<byte> message)
    {
        if (message.IsEmpty)
        {
            throw new InvalidMidiDataException("Empty MIDI message");
        }

        return message[0] switch
        {
            SysexMessage.SystemExclusive or SysexMessage.SpecialSystemExclusive => new SysexMessage(message.ToArray()),
            _ => ShortMessage.Parse(message),
        };
    }

    public override string ToString() => $"{GetType().Name}[{BitConverter.ToString(bytes)}]";
}

/// <summary>
/// A channel or system (non-exclusive) MIDI message of up to two data bytes.
/// </summary>
public sealed class ShortMessage : MidiMessage
{
    // Channel voice messages (status & 0xF0)
    public const int NoteOff = 0x80;
    public const int NoteOn = 0x90;
    public const int PolyPressure = 0xA0;
    public const int ControlChange = 0xB0;
    public const int ProgramChange = 0xC0;
    public const int ChannelPressure = 0xD0;
    public const int PitchBend = 0xE0;

    // System common & real-time messages (full status byte)
    public const int MidiTimeCode = 0xF1;
    public const int SongPositionPointer = 0xF2;
    public const int SongSelect = 0xF3;
    public const int TuneRequest = 0xF6;
    public const int EndOfExclusive = 0xF7;
    public const int TimingClock = 0xF8;
    public const int Start = 0xFA;
    public const int Continue = 0xFB;
    public const int Stop = 0xFC;
    public const int ActiveSensing = 0xFE;
    public const int SystemReset = 0xFF;

    private ShortMessage(byte[] bytes)
        : base(bytes)
    {
    }

    /// <summary>Creates a channel message.</summary>
    /// <param name="command">Command, e.g. <see cref="NoteOn"/>.</param>
    /// <param name="channel">Channel, 0-15.</param>
    /// <param name="data1">First data byte, 0-127.</param>
    /// <param name="data2">Second data byte, 0-127 (ignored for one-data-byte commands).</param>
    public ShortMessage(int command, int channel, int data1, int data2)
        : this(Build(ValidateCommand(command) | ValidateRange(channel, 15, nameof(channel)), data1, data2))
    {
    }

    /// <summary>Creates a message from a status byte and data bytes.</summary>
    public ShortMessage(int status, int data1, int data2)
        : this(Build(status, data1, data2))
    {
    }

    internal static ShortMessage Parse(ReadOnlySpan<byte> message)
    {
        int status = message[0];
        int dataLength = GetDataLength(status);
        if (dataLength < 0 || message.Length < 1 + dataLength)
        {
            throw new InvalidMidiDataException($"Invalid short message: {BitConverter.ToString(message.ToArray())}");
        }

        return new ShortMessage(message[..(1 + dataLength)].ToArray());
    }

    /// <summary>Wraps raw bytes that have already been validated (used by the file reader).</summary>
    internal static ShortMessage FromValidatedBytes(byte[] bytes) => new(bytes);

    /// <summary>Number of data bytes following the given status byte, or -1 if it's not a short message status.</summary>
    public static int GetDataLength(int status)
    {
        if (status is < 0x80 or > 0xFF)
        {
            return -1;
        }

        if (status < 0xF0)
        {
            return (status & 0xF0) is ProgramChange or ChannelPressure ? 1 : 2;
        }

        return status switch
        {
            MidiTimeCode or SongSelect => 1,
            SongPositionPointer => 2,
            0xF0 or EndOfExclusive => -1, // System exclusive
            _ => 0,
        };
    }

    /// <summary>The command (status &amp; 0xF0), e.g. <see cref="NoteOn"/>.</summary>
    public int Command => Status & 0xF0;

    /// <summary>The channel (0-15) for channel messages.</summary>
    public int Channel => Status & 0x0F;

    public int Data1 => bytes.Length > 1 ? bytes[1] : 0;

    public int Data2 => bytes.Length > 2 ? bytes[2] : 0;

    private static byte[] Build(int status, int data1, int data2)
    {
        int dataLength = GetDataLength(status);
        if (dataLength < 0)
        {
            throw new InvalidMidiDataException($"Invalid status byte for a short message: 0x{status:X2}");
        }

        return dataLength switch
        {
            0 => [(byte)status],
            1 => [(byte)status, (byte)ValidateRange(data1, 127, nameof(data1))],
            _ => [(byte)status, (byte)ValidateRange(data1, 127, nameof(data1)), (byte)ValidateRange(data2, 127, nameof(data2))],
        };
    }

    private static int ValidateCommand(int command) =>
        command is >= 0x80 and <= 0xE0 && (command & 0x0F) == 0
            ? command
            : throw new InvalidMidiDataException($"Invalid channel command: 0x{command:X2}");

    private static int ValidateRange(int value, int max, string name) =>
        value >= 0 && value <= max ? value : throw new InvalidMidiDataException($"{name} out of range: {value}");
}

/// <summary>
/// A system exclusive message.  <see cref="Data"/> excludes the status byte but includes any trailing 0xF7.
/// </summary>
public sealed class SysexMessage : MidiMessage
{
    public const int SystemExclusive = 0xF0;
    public const int SpecialSystemExclusive = 0xF7;

    internal SysexMessage(byte[] bytes)
        : base(bytes)
    {
    }

    /// <summary>Creates a system exclusive message.</summary>
    /// <param name="status"><see cref="SystemExclusive"/> or <see cref="SpecialSystemExclusive"/>.</param>
    /// <param name="data">Data following the status byte (normally ending in 0xF7).</param>
    public SysexMessage(int status, ReadOnlySpan<byte> data)
        : base(Build(status, data))
    {
    }

    public ReadOnlySpan<byte> Data => bytes.AsSpan(1);

    private static byte[] Build(int status, ReadOnlySpan<byte> data)
    {
        if (status is not (SystemExclusive or SpecialSystemExclusive))
        {
            throw new InvalidMidiDataException($"Invalid status byte for a sysex message: 0x{status:X2}");
        }

        var result = new byte[data.Length + 1];
        result[0] = (byte)status;
        data.CopyTo(result.AsSpan(1));
        return result;
    }
}

/// <summary>
/// A MIDI file meta event.  These are only ever found in sequences; they are not sent to MIDI devices.
/// </summary>
public sealed class MetaMessage : MidiMessage
{
    public const int Meta = 0xFF;
    public const int SequenceNumberType = 0x00;
    public const int TextType = 0x01;
    public const int TrackNameType = 0x03;
    public const int EndOfTrackType = 0x2F;
    public const int TempoType = 0x51;
    public const int TimeSignatureType = 0x58;
    public const int KeySignatureType = 0x59;

    public static readonly MetaMessage EndOfTrack = new(EndOfTrackType, []);

    private readonly byte[] data;

    public MetaMessage(int type, ReadOnlySpan<byte> data)
        : base(Build(type, data))
    {
        Type = type;
        this.data = data.ToArray();
    }

    /// <summary>The meta event type, e.g. <see cref="TempoType"/>.</summary>
    public int Type { get; }

    public ReadOnlySpan<byte> Data => data;

    /// <summary>Creates a tempo meta event.</summary>
    /// <param name="microsecondsPerQuarterNote">Tempo in microseconds per quarter note.</param>
    public static MetaMessage Tempo(int microsecondsPerQuarterNote) =>
        new(TempoType, [(byte)(microsecondsPerQuarterNote >> 16), (byte)(microsecondsPerQuarterNote >> 8), (byte)microsecondsPerQuarterNote]);

    /// <summary>If this is a tempo event, gets the tempo in microseconds per quarter note.</summary>
    public bool TryGetTempo(out int microsecondsPerQuarterNote)
    {
        if (Type == TempoType && data.Length >= 3)
        {
            microsecondsPerQuarterNote = (data[0] << 16) | (data[1] << 8) | data[2];
            return microsecondsPerQuarterNote > 0;
        }

        microsecondsPerQuarterNote = 0;
        return false;
    }

    private static byte[] Build(int type, ReadOnlySpan<byte> data)
    {
        if (type is < 0 or > 127)
        {
            throw new InvalidMidiDataException($"Invalid meta event type: {type}");
        }

        // FF <type> <variable-length size> <data>
        var result = new List<byte>(data.Length + 6) { Meta, (byte)type };
        result.AddRange(MidiFileReader.EncodeVariableLength(data.Length));
        result.AddRange(data.ToArray());
        return result.ToArray();
    }
}

/// <summary>
/// Thrown when MIDI data (e.g. a MIDI file) is invalid.
/// </summary>
public class InvalidMidiDataException : Exception
{
    public InvalidMidiDataException(string message)
        : base(message)
    {
    }

    public InvalidMidiDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
