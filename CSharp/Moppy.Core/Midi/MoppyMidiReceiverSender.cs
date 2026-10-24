using Microsoft.Extensions.Logging;
using Moppy.Core.Comms.Bridge;
using Moppy.Core.Events.Mapper;
using Moppy.Core.Events.PostProcessor;
using Moppy.Core.Status;

namespace Moppy.Core.Midi;

/// <summary>
/// Connects a MIDI source (a sequencer or MIDI In device) to a Moppy NetworkBridge: every MIDI message is mapped
/// to MoppyMessages, post-processed, and sent.  Also sends sequencer status changes (start / stop / reset) to the network.
/// </summary>
public sealed class MoppyMidiReceiverSender : StatusSender, IMidiReceiver
{
    private readonly MapperCollection<MidiMessage> mappers;
    private readonly IMessagePostProcessor postProcessor;
    private volatile IMidiReceiver? midiThru;

    public MoppyMidiReceiverSender(MapperCollection<MidiMessage> mapperCollection, IMessagePostProcessor postProcessor, NetworkBridge netBridge)
        : base(netBridge)
    {
        mappers = mapperCollection ?? throw new ArgumentNullException(nameof(mapperCollection));
        this.postProcessor = postProcessor ?? throw new ArgumentNullException(nameof(postProcessor));
    }

    /// <summary>
    /// A MIDI receiver to receive all (unmapped) MIDI messages as well, or <c>null</c> to disable MIDI thru.
    /// </summary>
    public IMidiReceiver? MidiThru
    {
        get => midiThru;
        set => midiThru = value;
    }

    public void Send(MidiMessage message, long timeStamp)
    {
        foreach (var mapped in mappers.MapEvent(message))
        {
            var messageToSend = postProcessor.PostProcess(mapped);
            try
            {
                NetworkBridge.SendMessage(messageToSend);
            }
            catch (IOException ex)
            {
                MoppyLogging.CreateLogger<MoppyMidiReceiverSender>().LogWarning(ex, "Could not send {Message}", messageToSend);
            }
        }

        // If a midiThru receiver has been specified, forward the message.
        var thru = midiThru;
        if (thru is not null)
        {
            try
            {
                thru.Send(message, timeStamp);
            }
            catch (Exception ex)
            {
                MoppyLogging.CreateLogger<MoppyMidiReceiverSender>().LogWarning(ex, "MIDI thru failed for {Message}", message);
            }
        }
    }
}
