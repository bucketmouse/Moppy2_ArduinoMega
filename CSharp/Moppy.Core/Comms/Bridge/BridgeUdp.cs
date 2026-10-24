using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Moppy.Core.Comms.Bridge;

/// <summary>
/// UDP-based network bridge for MoppyMessages.
/// </summary>
/// <remarks>
/// This bridge utilizes a multicast group to simplify communication to multiple Moppy devices on a network.
/// Ideally this should be an effectively zero-conf bridge and work out of the box on any normal network.
/// </remarks>
public sealed class BridgeUdp : NetworkBridge
{
    public const int MoppyPort = 30994;
    public static readonly IPAddress GroupAddress = IPAddress.Parse("239.2.2.7");

    private readonly object socketLock = new();
    private readonly IPEndPoint groupEndPoint = new(GroupAddress, MoppyPort);
    private volatile Socket? socket;
    private Thread? listenerThread;

    private static ILogger Log => MoppyLogging.CreateLogger<BridgeUdp>();

    public override void Connect()
    {
        lock (socketLock)
        {
            if (socket is not null)
            {
                return;
            }

            var newSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                // Allow other Moppy programs on this machine (e.g. a simulated device) to share the port
                newSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
                {
                    const int SolSocket = 0xffff, SoReusePort = 0x200;
                    newSocket.SetRawSocketOption(SolSocket, SoReusePort, BitConverter.GetBytes(1));
                }

                newSocket.Bind(new IPEndPoint(IPAddress.Any, MoppyPort));
                newSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(GroupAddress, IPAddress.Any));
                newSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            }
            catch (SocketException ex)
            {
                newSocket.Dispose();
                throw new IOException($"Failed to join multicast group {NetworkIdentifier}: {ex.Message}", ex);
            }

            socket = newSocket;

            // Create and start listener thread
            listenerThread = new Thread(() => Listen(newSocket))
            {
                IsBackground = true,
                Name = "Moppy UDP listener",
            };
            listenerThread.Start();
        }
    }

    public override void Connect(object? connectionOption) => Connect(); // Ignore argument since we have no options

    public override void SendMessage(MoppyMessage messageToSend)
    {
        var currentSocket = socket;
        if (currentSocket is null)
        {
            Log.LogDebug("UDP socket not connected");
            return; // We're not connected-- just silently fail.
        }

        try
        {
            currentSocket.SendTo(messageToSend.MessageBytes, SocketFlags.None, groupEndPoint);
        }
        catch (ObjectDisposedException)
        {
            // Closed while sending
        }
        catch (SocketException ex)
        {
            throw new IOException($"Failed to send UDP message: {ex.Message}", ex);
        }
    }

    public override void Close()
    {
        Socket? closingSocket;
        Thread? closingThread;
        lock (socketLock)
        {
            closingSocket = socket;
            closingThread = listenerThread;
            if (closingSocket is null)
            {
                return;
            }

            try
            {
                SendMessage(MoppyMessage.SysStop); // Send a stop message before closing to prevent sticking
                closingSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership,
                    new MulticastOption(GroupAddress, IPAddress.Any));
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                Log.LogWarning(ex, "Error while leaving multicast group");
            }
            finally
            {
                socket = null;
                listenerThread = null;
                closingSocket.Dispose(); // Unblocks the listener
            }
        }

        if (closingThread is not null && closingThread != Thread.CurrentThread)
        {
            closingThread.Join(TimeSpan.FromSeconds(2));
        }
    }

    public override string NetworkIdentifier => $"{GroupAddress}:{MoppyPort}";

    public override bool IsConnected => socket is not null;

    public override IReadOnlyList<object> ConnectionOptions => [];

    public override object? CurrentConnectionOption => null;

    private void Listen(Socket listenSocket)
    {
        // MoppyMessages can't be longer than 259 bytes (SOM, DEVADDR, SUBADDR, LEN, [0-255 body bytes])
        // Longer messages will be truncated, but we don't care about them anyway
        var buffer = new byte[MoppyMessage.MaxLength];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (ReferenceEquals(socket, listenSocket))
        {
            try
            {
                int received = listenSocket.ReceiveFrom(buffer, ref remote);
                if (received > 0 && buffer[0] == MoppyMessage.StartByte)
                {
                    AcceptNetworkMessage(MoppyMessageFactory.NetworkReceivedFromBytes(
                        buffer.AsSpan(0, received),
                        typeof(BridgeUdp).FullName!,
                        NetworkIdentifier,
                        ((IPEndPoint)remote).Address.ToString()));
                }
            }
            catch (Exception) when (!ReferenceEquals(socket, listenSocket))
            {
                break; // Socket was closed
            }
            catch (ArgumentException ex)
            {
                Log.LogWarning(ex, "Received malformed UDP message");
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // Windows reports ICMP port-unreachable / oversized datagrams on the next receive; ignore them
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Error receiving UDP message");
                Thread.Sleep(100);
            }
        }
    }
}
