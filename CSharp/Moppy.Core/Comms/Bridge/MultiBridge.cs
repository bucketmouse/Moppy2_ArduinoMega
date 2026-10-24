using Microsoft.Extensions.Logging;

namespace Moppy.Core.Comms.Bridge;

/// <summary>
/// A NetworkBridge implementation that supports multiple bridges.
/// </summary>
/// <remarks>
/// This allows, for example, multiple COM ports to be used, or a COM port to be used in conjunction
/// with a network bridge.  Messages sent through the MultiBridge go to every member bridge, and messages
/// received by any member are passed on to the MultiBridge's receivers.
/// </remarks>
public sealed class MultiBridge : NetworkBridge
{
    private readonly object bridgesLock = new();
    private NetworkBridge[] bridges = [];

    private static ILogger Log => MoppyLogging.CreateLogger<MultiBridge>();

    /// <summary>The bridges currently part of this group.</summary>
    public IReadOnlyList<NetworkBridge> Bridges => Volatile.Read(ref bridges);

    /// <summary>
    /// Adds a network bridge that will send/receive messages as part of the MultiBridge group.
    /// </summary>
    public void AddBridge(NetworkBridge bridgeToAdd)
    {
        ArgumentNullException.ThrowIfNull(bridgeToAdd);

        // Any messages received by the underlying bridges should be sent to the MultiBridge's receivers
        bridgeToAdd.RegisterMessageReceiver(this);
        lock (bridgesLock)
        {
            if (!bridges.Contains(bridgeToAdd))
            {
                bridges = [.. bridges, bridgeToAdd];
            }
        }
    }

    public void RemoveBridge(NetworkBridge bridgeToRemove)
    {
        bridgeToRemove.DeregisterMessageReceiver(this);
        lock (bridgesLock)
        {
            bridges = bridges.Where(b => !ReferenceEquals(b, bridgeToRemove)).ToArray();
        }
    }

    /// <summary>Connects every member bridge.</summary>
    /// <exception cref="IOException">Summarizes any bridges that failed to connect (others stay connected).</exception>
    public override void Connect()
    {
        var connectionExceptions = new List<Exception>();
        foreach (var bridge in Bridges)
        {
            try
            {
                bridge.Connect();
            }
            catch (IOException ex)
            {
                connectionExceptions.Add(ex);
                Log.LogWarning(ex, "Failed to connect {Bridge}", bridge);
            }
        }

        if (connectionExceptions.Count > 0)
        {
            throw new IOException(
                $"{connectionExceptions.Count} exceptions thrown during network connection:\n" +
                string.Join("\n", connectionExceptions.Select(ex => ex.Message)),
                new AggregateException(connectionExceptions));
        }
    }

    public override void Connect(object? connectionOption) => Connect(); // Ignore argument since we have no options

    public override void SendMessage(MoppyMessage messageToSend)
    {
        foreach (var bridge in Bridges)
        {
            try
            {
                bridge.SendMessage(messageToSend);
            }
            catch (IOException ex)
            {
                // Potentially only one of the network interfaces here is throwing an exception, so
                // we don't want to rethrow-- instead we'll make sure it's logged and keep going
                Log.LogWarning(ex, "Failed to send message on {Bridge}", bridge);
            }
        }
    }

    public override void Close()
    {
        foreach (var bridge in Bridges)
        {
            try
            {
                bridge.Close();
            }
            catch (Exception ex)
            {
                // There's not much we can do if it fails to close (it's probably already closed).
                // Just log it and move on.
                Log.LogWarning(ex, "Failed to close {Bridge}", bridge);
            }
        }
    }

    public override string NetworkIdentifier => nameof(MultiBridge);

    /// <summary>True if any member bridge is connected.</summary>
    public override bool IsConnected => Bridges.Any(b => b.IsConnected);

    public override IReadOnlyList<object> ConnectionOptions => [];

    public override object? CurrentConnectionOption => null;
}
