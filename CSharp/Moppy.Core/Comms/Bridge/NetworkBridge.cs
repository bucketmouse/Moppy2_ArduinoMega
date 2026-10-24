using Microsoft.Extensions.Logging;

namespace Moppy.Core.Comms.Bridge;

/// <summary>
/// A particular form of the Moppy network; used for sending and receiving MoppyMessages.
/// </summary>
/// <remarks>
/// Every bridge is also an <see cref="INetworkMessageConsumer"/>: messages passed to
/// <see cref="AcceptNetworkMessage"/> are forwarded to all registered receivers, so messages pass through
/// bridge classes (e.g. a <see cref="MultiBridge"/>) to the first non-bridge consumer.
/// </remarks>
public abstract class NetworkBridge : INetworkMessageConsumer, IDisposable
{
    private readonly object receiversLock = new();
    private INetworkMessageConsumer[] receivers = [];

    /// <summary>Connects using the given connection option (e.g. a baud rate); <c>null</c> uses the default.</summary>
    /// <exception cref="IOException">If the connection could not be established.</exception>
    public abstract void Connect(object? connectionOption);

    /// <summary>Connects using the default connection option.</summary>
    /// <exception cref="IOException">If the connection could not be established.</exception>
    public abstract void Connect();

    public abstract bool IsConnected { get; }

    /// <exception cref="IOException">If the message could not be sent.</exception>
    public abstract void SendMessage(MoppyMessage messageToSend);

    /// <summary>Unique identifier for this network (e.g. multicast IP:port or COM port name).</summary>
    public abstract string NetworkIdentifier { get; }

    /// <summary>Options that may be passed to <see cref="Connect(object?)"/> (may be empty).</summary>
    public abstract IReadOnlyList<object> ConnectionOptions { get; }

    public abstract object? CurrentConnectionOption { get; }

    /// <summary>Disconnects from the network.  A closed bridge may be connected again.</summary>
    public abstract void Close();

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    public void RegisterMessageReceiver(INetworkMessageConsumer messageConsumer)
    {
        ArgumentNullException.ThrowIfNull(messageConsumer);
        lock (receiversLock)
        {
            if (!receivers.Contains(messageConsumer))
            {
                receivers = [.. receivers, messageConsumer];
            }
        }
    }

    public void DeregisterMessageReceiver(INetworkMessageConsumer messageConsumer)
    {
        lock (receiversLock)
        {
            receivers = receivers.Where(r => !ReferenceEquals(r, messageConsumer)).ToArray();
        }
    }

    /// <summary>
    /// Called by network leaves to pass messages from the network on to all registered receivers.
    /// </summary>
    public virtual void AcceptNetworkMessage(NetworkReceivedMessage networkMessage)
    {
        foreach (var receiver in Volatile.Read(ref receivers))
        {
            try
            {
                receiver.AcceptNetworkMessage(networkMessage);
            }
            catch (Exception ex)
            {
                MoppyLogging.CreateLogger<NetworkBridge>().LogWarning(ex, "Message receiver threw while handling {Message}", networkMessage);
            }
        }
    }

    public override string ToString() => $"{GetType().Name}[{NetworkIdentifier}]";
}
