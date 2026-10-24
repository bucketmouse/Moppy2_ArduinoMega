using Moppy.Core.Comms;

namespace Moppy.Core.Device;

/// <summary>
/// Convenience base class that parses MoppyMessages into method calls, for implementing (e.g. simulated) devices.
/// </summary>
public abstract class MoppyDevice
{
    /// <summary>Returns true if this device is listening for the specified address.</summary>
    public abstract bool MatchesAddress(byte deviceAddress, byte subAddress);

    public void HandleMessage(MoppyMessage incomingMessage)
    {
        ArgumentNullException.ThrowIfNull(incomingMessage);
        if (incomingMessage.MessageBody.IsEmpty)
        {
            return;
        }

        if (incomingMessage.IsSystemMessage)
        {
            switch (incomingMessage.MessageCommandByte)
            {
                case CommandByte.SysPing:
                    GotSystemPing();
                    break;
                case CommandByte.SysReset:
                    SystemReset();
                    break;
                case CommandByte.SysStart:
                    SystemStart();
                    break;
                case CommandByte.SysStop:
                    SystemStop();
                    break;
            }
        }
        else if (MatchesAddress(incomingMessage.DeviceAddress, incomingMessage.SubAddress))
        {
            byte deviceAddress = incomingMessage.DeviceAddress;
            byte subAddress = incomingMessage.SubAddress;
            var payload = incomingMessage.MessageCommandPayload;
            switch (incomingMessage.MessageCommandByte)
            {
                case CommandByte.DevReset:
                    DeviceReset(deviceAddress, subAddress);
                    break;
                case CommandByte.DevPlayNote when payload.Length >= 1:
                    DevicePlayNote(deviceAddress, subAddress, payload[0], payload.Length >= 2 ? payload[1] : (byte)127);
                    break;
                case CommandByte.DevStopNote when payload.Length >= 1:
                    DeviceStopNote(deviceAddress, subAddress, payload[0]);
                    break;
                case CommandByte.DevBendPitch when payload.Length >= 2:
                    DeviceBendPitch(deviceAddress, subAddress, (short)((payload[0] << 8) | payload[1]));
                    break;
            }
        }
        // Else this message wasn't for us
    }

    // System message handlers

    /// <summary>Override to respond to system pings (usually by sending a pong).</summary>
    public virtual void GotSystemPing() { }

    /// <summary>Override to provide system reset functionality.</summary>
    public virtual void SystemReset() { }

    /// <summary>Override to respond to the sequencer starting.</summary>
    public virtual void SystemStart() { }

    /// <summary>Override to respond to the sequencer stopping.</summary>
    public virtual void SystemStop() { }

    // Device message handlers

    /// <summary>Override to provide reset functionality.</summary>
    public virtual void DeviceReset(byte deviceAddress, byte subAddress) { }

    /// <summary>Override to provide play note functionality.</summary>
    public virtual void DevicePlayNote(byte deviceAddress, byte subAddress, byte noteNumber, byte velocity) { }

    /// <summary>Override to provide stop note functionality.</summary>
    public virtual void DeviceStopNote(byte deviceAddress, byte subAddress, byte noteNumber) { }

    /// <summary>Override to provide pitch bending functionality (-8192 to 8191, 0 = no bend).</summary>
    public virtual void DeviceBendPitch(byte deviceAddress, byte subAddress, short bendAmount) { }
}
