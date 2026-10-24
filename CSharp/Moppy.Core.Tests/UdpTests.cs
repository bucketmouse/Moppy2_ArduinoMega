using Moppy.Core.Comms;
using Moppy.Core.Comms.Bridge;
using Moppy.Core.Control;
using Moppy.Core.Device;
using Moppy.Core.Status;

namespace Moppy.Core.Tests;

/// <summary>
/// End-to-end over real UDP multicast on this machine: a simulated device answers pings, and receives notes.
/// </summary>
public class UdpTests
{
    [Fact]
    public void ControllerDiscoversAndPlaysToSimulatedUdpDevice()
    {
        using var device = new SimulatedDevice(deviceAddress: 3, drives: 4);
        device.Connect();

        var bus = new StatusBus();
        using var network = new NetworkManager(bus, includeUdp: true, () => []);
        network.ConnectBridge($"{BridgeUdp.GroupAddress}:{BridgeUdp.MoppyPort}", null);
        network.Start();

        Assert.True(SpinWait.SpinUntil(() => network.RecentlySeenDevices.Count > 0, TimeSpan.FromSeconds(10)),
            "Device was not discovered (is multicast available on this machine?)");
        var seen = network.RecentlySeenDevices.Single();
        Assert.Equal(3, seen.DeviceAddress);
        Assert.Equal(1, seen.MinSubAddress);
        Assert.Equal(4, seen.MaxSubAddress);
        Assert.StartsWith("239.2.2.7:30994 - ", seen.NetworkAddress);

        network.PrimaryBridge.SendMessage(MoppyMessageFactory.DevicePlayNote(3, 2, 60, 100));
        Assert.True(SpinWait.SpinUntil(() => device.HasPlayed(2, 60), TimeSpan.FromSeconds(5)));

        network.CloseBridge($"{BridgeUdp.GroupAddress}:{BridgeUdp.MoppyPort}");
        Assert.False(network.PrimaryBridge.IsConnected);
    }

    private sealed class SimulatedDevice(byte deviceAddress, byte drives) : MoppyDevice, INetworkMessageConsumer, IDisposable
    {
        private readonly BridgeUdp network = new();

        private readonly List<(int SubAddress, int Note)> played = [];

        public bool HasPlayed(int subAddress, int note)
        {
            lock (played)
            {
                return played.Contains((subAddress, note));
            }
        }

        public void Connect()
        {
            network.RegisterMessageReceiver(this);
            network.Connect();
        }

        public void Dispose() => network.Close();

        public override bool MatchesAddress(byte address, byte subAddress) => address == deviceAddress && subAddress <= drives;

        public void AcceptNetworkMessage(NetworkReceivedMessage networkMessage) => HandleMessage(networkMessage);

        public override void GotSystemPing() => network.SendMessage(MoppyMessageFactory.SystemPong(deviceAddress, 1, drives));

        public override void DevicePlayNote(byte address, byte subAddress, byte noteNumber, byte velocity)
        {
            lock (played)
            {
                played.Add((subAddress, noteNumber));
            }
        }
    }
}
