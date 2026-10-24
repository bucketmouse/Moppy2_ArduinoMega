namespace Moppy.Core.Comms;

/// <summary>
/// Command byte values used in <see cref="MoppyMessage"/>s.  These match the NETBYTE_* definitions in the
/// microcontroller firmware (MoppyNetworks/MoppyNetwork.h).
/// </summary>
public static class CommandByte
{
    // System messages (device address 0x00)
    public const byte SysPing = 0x80;
    public const byte SysPong = 0x81;
    public const byte SysReset = 0xff;
    public const byte SysStart = 0xfa;
    public const byte SysStop = 0xfc;

    // Device messages
    public const byte DevReset = 0x00;
    public const byte DevPlayNote = 0x09;
    public const byte DevStopNote = 0x08;
    public const byte DevBendPitch = 0x0e;
}

/// <summary>
/// Represents a message suitable for sending to a Moppy network.
/// </summary>
/// <remarks>
/// Messages are laid out as:
/// <code>
///  0    - START_BYTE (always 0x4d)
///  1    - Device address (0x00 for system-wide messages)
///  2    - Sub address (ignored for system-wide messages)
///  3    - Size of message body (number of bytes following this one)
///  4    - Command byte
///  5... - Optional payload
/// </code>
/// Two messages are equal if their bytes are equal.
/// </remarks>
public class MoppyMessage : IEquatable<MoppyMessage>
{
    public const byte StartByte = 0x4d;
    public const byte SystemAddress = 0x00;

    /// <summary>MoppyMessages can't be longer than 259 bytes (SOM, DEVADDR, SUBADDR, LEN, [0-255 body bytes]).</summary>
    public const int MaxLength = 4 + 255;

    public static readonly MoppyMessage SysPing = SystemMessage(CommandByte.SysPing);
    public static readonly MoppyMessage SysReset = SystemMessage(CommandByte.SysReset);
    public static readonly MoppyMessage SysStart = SystemMessage(CommandByte.SysStart);
    public static readonly MoppyMessage SysStop = SystemMessage(CommandByte.SysStop);

    private readonly byte[] messageBytes;

    /// <param name="messageBytes">Raw message bytes.  The array is owned by the message and must not be modified afterwards.</param>
    protected internal MoppyMessage(byte[] messageBytes)
    {
        ArgumentNullException.ThrowIfNull(messageBytes);
        if (messageBytes.Length < 5)
        {
            throw new ArgumentException("Not enough bytes for a MoppyMessage!", nameof(messageBytes));
        }

        this.messageBytes = messageBytes;
    }

    private static MoppyMessage SystemMessage(byte command) =>
        new([StartByte, SystemAddress, 0x00, 0x01, command]);

    /// <summary>Raw bytes that make up the message.</summary>
    public ReadOnlySpan<byte> MessageBytes => messageBytes;

    /// <summary>Total length of the message in bytes.</summary>
    public int Length => messageBytes.Length;

    /// <summary>Returns a copy of the raw bytes that make up the message.</summary>
    public byte[] ToArray() => (byte[])messageBytes.Clone();

    /// <summary>Target device address of the message.</summary>
    public byte DeviceAddress => messageBytes[1];

    public bool IsSystemMessage => DeviceAddress == SystemAddress;

    /// <exception cref="InvalidOperationException">If this is a system message.</exception>
    public byte SubAddress => IsSystemMessage
        ? throw new InvalidOperationException("This is a system message and has no sub-address")
        : messageBytes[2];

    /// <summary>
    /// The body of the message, which includes the command byte and any additional bytes of command payload.
    /// </summary>
    public ReadOnlySpan<byte> MessageBody
    {
        get
        {
            int bodyLength = Math.Min(messageBytes[3], messageBytes.Length - 4);
            return messageBytes.AsSpan(4, bodyLength);
        }
    }

    /// <exception cref="InvalidOperationException">If the message body is empty.</exception>
    public byte MessageCommandByte
    {
        get
        {
            var body = MessageBody;
            return body.Length > 0 ? body[0] : throw new InvalidOperationException("Message has an empty body");
        }
    }

    /// <summary>Just the variable command payload at the end of the message (may be zero-length!).</summary>
    public ReadOnlySpan<byte> MessageCommandPayload
    {
        get
        {
            var body = MessageBody;
            return body.Length > 0 ? body[1..] : [];
        }
    }

    public bool Equals(MoppyMessage? other) =>
        other is not null && messageBytes.AsSpan().SequenceEqual(other.messageBytes);

    public override bool Equals(object? obj) => Equals(obj as MoppyMessage);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(messageBytes);
        return hash.ToHashCode();
    }

    public override string ToString() => $"MoppyMessage[{BitConverter.ToString(messageBytes)}]";
}
