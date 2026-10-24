using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Moppy.Core.Comms;
using Moppy.Core.Comms.Bridge;
using Moppy.Core.Device;
using Moppy.Core.Status;

namespace Moppy.Core.Control;

/// <summary>
/// Manages connecting, disconnecting, and status for the controller's network connection(s), and discovers
/// devices on the connected networks by pinging them.
/// </summary>
/// <remarks>
/// Changes are reported on the <see cref="StatusBus"/> as <see cref="StatusType.NetStatusChanged"/> (a bridge
/// connected or disconnected, or the set of bridges changed) and <see cref="StatusType.NetDevicesChanged"/>.
/// </remarks>
public sealed class NetworkManager : INetworkMessageConsumer, IDisposable
{
    /// <summary>How often devices are pinged.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(3);

    /// <summary>Devices not heard from for this long are dropped from <see cref="RecentlySeenDevices"/>.</summary>
    public static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(7);

    private readonly StatusBus statusBus;
    private readonly MultiBridge multiBridge = new();
    private readonly object bridgesLock = new();
    private readonly OrderedDictionary<string, NetworkBridge> networkBridges = new();
    private readonly ConcurrentDictionary<DeviceDescriptor, DateTime> recentlySeenDevices = new();
    private readonly Func<IReadOnlyList<string>> serialPortLister;
    private CancellationTokenSource? pingerCancellation;
    private Thread? pingerThread;

    /// <summary>Creates a manager with a UDP bridge plus a serial bridge for each serial port on this machine.</summary>
    public NetworkManager(StatusBus statusBus)
        : this(statusBus, includeUdp: true, BridgeSerial.GetAvailableSerials)
    {
    }

    /// <param name="statusBus">StatusBus for updating local components about network changes.</param>
    /// <param name="includeUdp">Whether to offer a UDP multicast bridge.</param>
    /// <param name="serialPortLister">Lists serial ports to offer bridges for (e.g. <see cref="BridgeSerial.GetAvailableSerials"/>).</param>
    public NetworkManager(StatusBus statusBus, bool includeUdp, Func<IReadOnlyList<string>> serialPortLister)
    {
        this.statusBus = statusBus ?? throw new ArgumentNullException(nameof(statusBus));
        this.serialPortLister = serialPortLister ?? throw new ArgumentNullException(nameof(serialPortLister));
        multiBridge.RegisterMessageReceiver(this); // Register to receive network messages to look for pongs

        if (includeUdp)
        {
            AddBridgeInternal(new BridgeUdp());
        }

        foreach (string serial in serialPortLister())
        {
            AddBridgeInternal(new BridgeSerial(serial));
        }
    }

    private static ILogger Log => MoppyLogging.CreateLogger<NetworkManager>();

    /// <summary>
    /// For the purposes of being able to send or register to receive messages, the primary NetworkBridge being
    /// managed (a <see cref="MultiBridge"/> of all connected bridges).
    /// </summary>
    public NetworkBridge PrimaryBridge => multiBridge;

    /// <summary>Starts pinging the network for devices.</summary>
    public void Start()
    {
        lock (bridgesLock)
        {
            if (pingerThread is not null)
            {
                return;
            }

            pingerCancellation = new CancellationTokenSource();
            var token = pingerCancellation.Token;
            pingerThread = new Thread(() => RunPinger(token)) { IsBackground = true, Name = "Moppy network pinger" };
            pingerThread.Start();
        }
    }

    /// <summary>Stops pinging the network.</summary>
    public void Stop()
    {
        Thread? thread;
        lock (bridgesLock)
        {
            pingerCancellation?.Cancel();
            thread = pingerThread;
            pingerThread = null;
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// The NetworkManager is primarily concerned with receiving pong messages from the network so that it can
    /// update its current list of devices.
    /// </summary>
    public void AcceptNetworkMessage(NetworkReceivedMessage networkMessage)
    {
        // Whenever we receive a pong from one of the networks, update the device in
        // recentlySeenDevices with the time it was last seen
        if (!networkMessage.IsSystemMessage || networkMessage.MessageBody.IsEmpty ||
            networkMessage.MessageCommandByte != CommandByte.SysPong)
        {
            return;
        }

        var payload = networkMessage.MessageCommandPayload;
        if (payload.Length < 3)
        {
            return;
        }

        var device = new DeviceDescriptor(
            $"{networkMessage.NetworkIdentifier} - {networkMessage.RemoteIdentifier}",
            payload[0],
            payload[1],
            payload[2]);

        // If this device had never been seen before, we have a new device!  Let everyone know!
        if (recentlySeenDevices.TryAdd(device, DateTime.UtcNow))
        {
            statusBus.ReceiveUpdate(StatusUpdate.NetDevicesChanged);
        }
        else
        {
            recentlySeenDevices[device] = DateTime.UtcNow;
        }
    }

    /// <summary>Available network bridges, keyed by their unique network identifier (in display order).</summary>
    public IReadOnlyDictionary<string, NetworkBridge> AvailableNetworkBridges
    {
        get
        {
            lock (bridgesLock)
            {
                return new OrderedDictionary<string, NetworkBridge>(networkBridges);
            }
        }
    }

    /// <summary>Devices from which we've recently received a pong.</summary>
    public IReadOnlyList<DeviceDescriptor> RecentlySeenDevices =>
        recentlySeenDevices.Keys
            .OrderBy(d => d.NetworkAddress, StringComparer.Ordinal)
            .ThenBy(d => d.DeviceAddress)
            .ToArray();

    /// <summary>Adds a custom bridge to the list of available bridges.</summary>
    public void AddBridge(NetworkBridge bridge)
    {
        lock (bridgesLock)
        {
            AddBridgeInternal(bridge);
        }

        statusBus.ReceiveUpdate(StatusUpdate.NetStatusChanged);
    }

    /// <summary>
    /// Re-scans serial ports: bridges are added for new ports, and removed for ports that disappeared (unless connected).
    /// </summary>
    public void RefreshAvailableBridges()
    {
        var ports = serialPortLister();
        bool changed = false;
        lock (bridgesLock)
        {
            foreach (var (id, bridge) in networkBridges.ToArray())
            {
                if (bridge is BridgeSerial && !bridge.IsConnected && !ports.Contains(id))
                {
                    networkBridges.Remove(id);
                    changed = true;
                }
            }

            foreach (string port in ports)
            {
                if (!networkBridges.ContainsKey(port))
                {
                    AddBridgeInternal(new BridgeSerial(port));
                    changed = true;
                }
            }
        }

        if (changed)
        {
            statusBus.ReceiveUpdate(StatusUpdate.NetStatusChanged);
        }
    }

    /// <summary>Connects a bridge and adds it to <see cref="PrimaryBridge"/>.</summary>
    /// <param name="bridgeIdentifier">The bridge's network identifier.</param>
    /// <param name="connectionOption">Connection option (e.g. baud rate), or null for the default.</param>
    /// <exception cref="IOException">If the bridge failed to connect.</exception>
    public void ConnectBridge(string bridgeIdentifier, object? connectionOption)
    {
        try
        {
            var bridge = GetBridge(bridgeIdentifier);
            bridge.Connect(connectionOption);
            multiBridge.AddBridge(bridge);
        }
        finally
        {
            statusBus.ReceiveUpdate(StatusUpdate.NetStatusChanged);
        }
    }

    /// <summary>Removes a bridge from <see cref="PrimaryBridge"/> and disconnects it.</summary>
    public void CloseBridge(string bridgeIdentifier)
    {
        try
        {
            var bridge = GetBridge(bridgeIdentifier);
            multiBridge.RemoveBridge(bridge);
            bridge.Close();
        }
        finally
        {
            statusBus.ReceiveUpdate(StatusUpdate.NetStatusChanged);
        }
    }

    /// <summary>Stops pinging and closes all connected bridges.</summary>
    public void Dispose()
    {
        Stop();
        multiBridge.Close();
    }

    private NetworkBridge GetBridge(string bridgeIdentifier)
    {
        lock (bridgesLock)
        {
            return networkBridges.TryGetValue(bridgeIdentifier, out var bridge)
                ? bridge
                : throw new ArgumentException($"Unknown network bridge '{bridgeIdentifier}'", nameof(bridgeIdentifier));
        }
    }

    private void AddBridgeInternal(NetworkBridge bridge)
    {
        if (!networkBridges.TryAdd(bridge.NetworkIdentifier, bridge))
        {
            throw new ArgumentException($"A bridge with identifier '{bridge.NetworkIdentifier}' already exists", nameof(bridge));
        }
    }

    /// <summary>
    /// Occasionally pings the network looking for new devices.  Also culls devices not seen for a while from the list.
    /// </summary>
    private void RunPinger(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // Send a ping
            try
            {
                multiBridge.SendMessage(MoppyMessage.SysPing);
            }
            catch (Exception ex)
            {
                // If for some reason we can't send the message, just log and carry on (hopefully whatever's wrong
                // will resolve itself again, but we don't want to kill the pinger)
                Log.LogWarning(ex, "Failed to ping network");
            }

            // Wait a bit for responses and because we don't need to ping constantly
            if (token.WaitHandle.WaitOne(PingInterval))
            {
                break;
            }

            // Cull devices not seen recently
            bool devicesCulled = false;
            var cutoff = DateTime.UtcNow - DeviceTimeout;
            foreach (var entry in recentlySeenDevices)
            {
                if (entry.Value < cutoff && recentlySeenDevices.TryRemove(entry))
                {
                    devicesCulled = true;
                }
            }

            if (devicesCulled)
            {
                statusBus.ReceiveUpdate(StatusUpdate.NetDevicesChanged);
            }
        }
    }
}
