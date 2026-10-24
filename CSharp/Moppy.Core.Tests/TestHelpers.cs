using Moppy.Core.Comms;
using Moppy.Core.Comms.Bridge;
using Moppy.Core.Midi;

namespace Moppy.Core.Tests;

internal static class TestHelpers
{
    public static string SampleSong(string name) => Path.Combine(AppContext.BaseDirectory, "samplesongs", name);

    public static ShortMessage NoteOn(int channel, int note, int velocity) => new(ShortMessage.NoteOn, channel, note, velocity);

    public static ShortMessage NoteOff(int channel, int note) => new(ShortMessage.NoteOff, channel, note, 0);

    public static MidiSequence Sequence(int resolution, params MidiEvent[] events) =>
        new(DivisionType.Ppq, resolution, [new MidiTrack(events)]);
}

/// <summary>Records everything sent to it, with the time it arrived.</summary>
internal sealed class RecordingReceiver : IMidiReceiver
{
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<(TimeSpan Time, MidiMessage Message)> received = [];

    public IReadOnlyList<(TimeSpan Time, MidiMessage Message)> Received
    {
        get
        {
            lock (received)
            {
                return received.ToArray();
            }
        }
    }

    public IReadOnlyList<ShortMessage> Notes => Received
        .Select(r => r.Message)
        .OfType<ShortMessage>()
        .Where(m => m.Command is ShortMessage.NoteOn or ShortMessage.NoteOff)
        .ToArray();

    public void Send(MidiMessage message, long timeStamp)
    {
        lock (received)
        {
            received.Add((clock.Elapsed, message));
        }
    }
}

/// <summary>A bridge that records sent messages, and lets tests inject received ones.</summary>
internal sealed class FakeBridge(string id) : NetworkBridge
{
    private readonly List<MoppyMessage> sent = [];
    private bool connected;

    public IReadOnlyList<MoppyMessage> Sent
    {
        get
        {
            lock (sent)
            {
                return sent.ToArray();
            }
        }
    }

    public override void Connect(object? connectionOption) => connected = true;

    public override void Connect() => connected = true;

    public override bool IsConnected => connected;

    public override void SendMessage(MoppyMessage messageToSend)
    {
        lock (sent)
        {
            sent.Add(messageToSend);
        }
    }

    public override string NetworkIdentifier => id;

    public override IReadOnlyList<object> ConnectionOptions => [];

    public override object? CurrentConnectionOption => null;

    public override void Close() => connected = false;

    public void Receive(MoppyMessage message) =>
        AcceptNetworkMessage(MoppyMessageFactory.NetworkReceivedFromBytes(message.MessageBytes, "Fake", id, "remote"));
}
