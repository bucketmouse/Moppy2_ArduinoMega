using static Moppy.Core.Comms.MoppyMessage;

namespace Moppy.Core.Comms;

/// <summary>
/// Builds common <see cref="MoppyMessage"/>s.
/// </summary>
public static class MoppyMessageFactory
{
    // System messages

    public static MoppyMessage SystemPong(byte deviceAddress, byte minSubAddress, byte maxSubAddress) =>
        new([StartByte, SystemAddress, 0x00, 0x04, CommandByte.SysPong, deviceAddress, minSubAddress, maxSubAddress]);

    // Device messages

    public static MoppyMessage DeviceReset(byte deviceAddress) =>
        new([StartByte, deviceAddress, 0x00, 0x01, CommandByte.DevReset]);

    public static MoppyMessage DevicePlayNote(byte deviceAddress, byte subAddress, byte noteNumber, byte noteVelocity) =>
        new([StartByte, deviceAddress, subAddress, 0x03, CommandByte.DevPlayNote, noteNumber, noteVelocity]);

    public static MoppyMessage DeviceStopNote(byte deviceAddress, byte subAddress, byte noteNumber) =>
        new([StartByte, deviceAddress, subAddress, 0x02, CommandByte.DevStopNote, noteNumber]);

    /// <summary>Builds a pitch bend message.</summary>
    /// <param name="deviceAddress">Target device address.</param>
    /// <param name="subAddress">Target sub-address.</param>
    /// <param name="bendAmount">Signed bend amount where 0 = no bend, with a range of -8192 to 8191.</param>
    public static MoppyMessage DevicePitchBend(byte deviceAddress, byte subAddress, short bendAmount) =>
        new([StartByte, deviceAddress, subAddress, 0x03, CommandByte.DevBendPitch,
            (byte)((bendAmount >> 8) & 0xff), (byte)(bendAmount & 0xff)]);

    /// <summary>
    /// Builds a message with an arbitrary command byte and payload (e.g. for device-specific commands).
    /// </summary>
    public static MoppyMessage DeviceCommand(byte deviceAddress, byte subAddress, byte command, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 254)
        {
            throw new ArgumentException("Payload can't be longer than 254 bytes", nameof(payload));
        }

        var bytes = new byte[5 + payload.Length];
        bytes[0] = StartByte;
        bytes[1] = deviceAddress;
        bytes[2] = subAddress;
        bytes[3] = (byte)(1 + payload.Length);
        bytes[4] = command;
        payload.CopyTo(bytes.AsSpan(5));
        return new MoppyMessage(bytes);
    }

    /// <summary>
    /// Not a recommended way to create well-structured MoppyMessages, but available primarily for
    /// NetworkBridges to take advantage of.  No validation beyond a minimum length is performed!
    /// </summary>
    public static MoppyMessage FromBytes(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());

    public static NetworkReceivedMessage NetworkReceivedFromBytes(ReadOnlySpan<byte> bytes, string networkType,
        string networkIdentifier, string remoteIdentifier) =>
        new(bytes.ToArray(), networkType, networkIdentifier, remoteIdentifier);
}
