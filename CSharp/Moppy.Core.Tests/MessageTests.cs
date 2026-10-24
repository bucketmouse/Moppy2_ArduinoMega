using Moppy.Core.Comms;
using Moppy.Core.Control;
using Moppy.Core.Device;
using Moppy.Core.Events.Mapper;
using Moppy.Core.Status;

namespace Moppy.Core.Tests;

public class MessageTests
{
    [Fact]
    public void SystemMessagesMatchFirmwareProtocol()
    {
        Assert.Equal([0x4d, 0x00, 0x00, 0x01, 0x80], MoppyMessage.SysPing.ToArray());
        Assert.Equal([0x4d, 0x00, 0x00, 0x01, 0xff], MoppyMessage.SysReset.ToArray());
        Assert.Equal([0x4d, 0x00, 0x00, 0x01, 0xfa], MoppyMessage.SysStart.ToArray());
        Assert.Equal([0x4d, 0x00, 0x00, 0x01, 0xfc], MoppyMessage.SysStop.ToArray());
        Assert.True(MoppyMessage.SysPing.IsSystemMessage);
        Assert.Throws<InvalidOperationException>(() => MoppyMessage.SysPing.SubAddress);
    }

    [Fact]
    public void FactoryBuildsDeviceMessages()
    {
        Assert.Equal([0x4d, 1, 2, 3, 0x09, 60, 100], MoppyMessageFactory.DevicePlayNote(1, 2, 60, 100).ToArray());
        Assert.Equal([0x4d, 1, 2, 2, 0x08, 60], MoppyMessageFactory.DeviceStopNote(1, 2, 60).ToArray());
        Assert.Equal([0x4d, 1, 2, 3, 0x0e, 0xE0, 0x00], MoppyMessageFactory.DevicePitchBend(1, 2, -8192).ToArray());
        Assert.Equal([0x4d, 1, 2, 3, 0x0e, 0x1F, 0xFF], MoppyMessageFactory.DevicePitchBend(1, 2, 8191).ToArray());
        Assert.Equal([0x4d, 3, 0, 1, 0x00], MoppyMessageFactory.DeviceReset(3).ToArray());
        Assert.Equal([0x4d, 0, 0, 4, 0x81, 1, 1, 8], MoppyMessageFactory.SystemPong(1, 1, 8).ToArray());

        var message = MoppyMessageFactory.DevicePlayNote(1, 2, 60, 100);
        Assert.Equal(1, message.DeviceAddress);
        Assert.Equal(2, message.SubAddress);
        Assert.Equal(CommandByte.DevPlayNote, message.MessageCommandByte);
        Assert.Equal([60, 100], message.MessageCommandPayload.ToArray());
        Assert.Equal(message, MoppyMessageFactory.FromBytes(message.MessageBytes));
    }

    [Fact]
    public void MessagesNeedAtLeastFiveBytes() =>
        Assert.Throws<ArgumentException>(() => MoppyMessageFactory.FromBytes([0x4d, 0, 0, 0]));

    [Theory]
    [InlineData(StatusType.SequenceStart, null, new byte[] { 0xfa })]
    [InlineData(StatusType.SequencePause, null, new byte[] { 0xfc })]
    [InlineData(StatusType.SequenceEnd, false, new byte[] { 0xfc })]
    [InlineData(StatusType.SequenceEnd, true, new byte[] { 0xfc, 0xff })]
    [InlineData(StatusType.SequenceStopped, true, new byte[] { 0xfc, 0xff })]
    [InlineData(StatusType.NetDevicesChanged, null, new byte[0])]
    public void StatusUpdatesMapToSystemMessages(StatusType type, bool? reset, byte[] expectedCommands)
    {
        var update = type switch
        {
            StatusType.SequenceStart => StatusUpdate.SequenceStart,
            StatusType.SequencePause => StatusUpdate.SequencePause,
            StatusType.SequenceEnd => StatusUpdate.SequenceEnd(reset!.Value),
            StatusType.SequenceStopped => StatusUpdate.SequenceStopped,
            _ => StatusUpdate.NetDevicesChanged,
        };

        Assert.Equal(expectedCommands, BaseEventMapper.MapStatusUpdate(update).Select(m => m.MessageCommandByte).ToArray());
    }

    [Theory]
    [InlineData(false, 1.0, 100, 100)]
    [InlineData(false, 0.5, 100, 50)]
    [InlineData(true, 1.0, 10, 127)]
    [InlineData(true, 0.5, 10, 63)]
    [InlineData(false, 0.0, 100, 0)]
    public void VelocityPostProcessorScalesOrOverridesVolume(bool overrideVelocity, double multiplier, byte velocity, byte expected)
    {
        var processor = new VelocityPostProcessor { OverrideVelocity = overrideVelocity, VelocityMultiplier = multiplier };
        var result = processor.PostProcess(MoppyMessageFactory.DevicePlayNote(1, 1, 60, velocity));
        Assert.Equal(expected, result.MessageBytes[6]);

        // Other messages are untouched
        var stop = MoppyMessageFactory.DeviceStopNote(1, 1, 60);
        Assert.Same(stop, processor.PostProcess(stop));
    }

    [Fact]
    public void MoppyDeviceDispatchesMessages()
    {
        var device = new RecordingDevice();
        device.HandleMessage(MoppyMessage.SysPing);
        device.HandleMessage(MoppyMessage.SysReset);
        device.HandleMessage(MoppyMessageFactory.DevicePlayNote(1, 2, 60, 90));
        device.HandleMessage(MoppyMessageFactory.DevicePlayNote(2, 2, 61, 90)); // Not our address
        device.HandleMessage(MoppyMessageFactory.DeviceStopNote(1, 2, 60));
        device.HandleMessage(MoppyMessageFactory.DevicePitchBend(1, 2, -200));

        Assert.Equal(["ping", "reset", "play 2 60 90", "stop 2 60", "bend 2 -200"], device.Calls);
    }

    private sealed class RecordingDevice : MoppyDevice
    {
        public List<string> Calls { get; } = [];

        public override bool MatchesAddress(byte deviceAddress, byte subAddress) => deviceAddress == 1;

        public override void GotSystemPing() => Calls.Add("ping");

        public override void SystemReset() => Calls.Add("reset");

        public override void DevicePlayNote(byte deviceAddress, byte subAddress, byte noteNumber, byte velocity) =>
            Calls.Add($"play {subAddress} {noteNumber} {velocity}");

        public override void DeviceStopNote(byte deviceAddress, byte subAddress, byte noteNumber) =>
            Calls.Add($"stop {subAddress} {noteNumber}");

        public override void DeviceBendPitch(byte deviceAddress, byte subAddress, short bendAmount) =>
            Calls.Add($"bend {subAddress} {bendAmount}");
    }
}
