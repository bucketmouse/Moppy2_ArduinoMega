namespace Moppy.Core.Comms;

/// <summary>
/// Interface for any classes that want to receive messages from the Moppy Network.
/// </summary>
public interface INetworkMessageConsumer
{
    void AcceptNetworkMessage(NetworkReceivedMessage networkMessage);
}

/// <summary>
/// Because messages themselves don't carry any network-specific info, this subclass
/// adds that info for use locally.
/// </summary>
public class NetworkReceivedMessage : MoppyMessage
{
    public NetworkReceivedMessage(byte[] messageBytes, string networkType, string networkIdentifier, string remoteIdentifier)
        : base(messageBytes)
    {
        NetworkType = networkType;
        NetworkIdentifier = networkIdentifier;
        RemoteIdentifier = remoteIdentifier;
    }

    /// <summary>Type of network (e.g. BridgeUdp or BridgeSerial).</summary>
    public string NetworkType { get; }

    /// <summary>Unique identifier for this network (e.g. multicast IP:port or COM port name).</summary>
    public string NetworkIdentifier { get; }

    /// <summary>Identifier of remote device on the network if available (e.g. IP address).</summary>
    public string RemoteIdentifier { get; }
}
