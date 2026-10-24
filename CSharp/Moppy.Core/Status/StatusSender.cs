using Microsoft.Extensions.Logging;
using Moppy.Core.Comms.Bridge;
using Moppy.Core.Events.Mapper;

namespace Moppy.Core.Status;

/// <summary>
/// Sends local control / sequencer events (e.g. start, stop, reset) to the network bridge.
/// </summary>
public abstract class StatusSender : IStatusConsumer
{
    protected StatusSender(NetworkBridge networkBridge)
    {
        NetworkBridge = networkBridge ?? throw new ArgumentNullException(nameof(networkBridge));
    }

    public NetworkBridge NetworkBridge { get; }

    public virtual void ReceiveUpdate(StatusUpdate update)
    {
        foreach (var messageToSend in BaseEventMapper.MapStatusUpdate(update))
        {
            try
            {
                NetworkBridge.SendMessage(messageToSend);
            }
            catch (IOException ex)
            {
                MoppyLogging.CreateLogger<StatusSender>().LogWarning(ex, "Could not send {Message}", messageToSend);
            }
        }
    }
}
