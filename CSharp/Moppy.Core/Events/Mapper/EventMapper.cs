using Moppy.Core.Comms;
using Moppy.Core.Status;

namespace Moppy.Core.Events.Mapper;

/// <summary>
/// Maps input events (e.g. MIDI events) to MoppyMessages.
/// </summary>
/// <typeparam name="TInputEvent">Type of input event to be mapped to MoppyMessages.</typeparam>
public interface IEventMapper<in TInputEvent>
{
    /// <returns>The message to send, or <c>null</c> if this mapper doesn't handle the event.</returns>
    MoppyMessage? MapEvent(TInputEvent inputEvent);
}

/// <summary>
/// Maps StatusUpdates to network events.
/// </summary>
public static class BaseEventMapper
{
    public static IReadOnlyList<MoppyMessage> MapStatusUpdate(StatusUpdate statusUpdate) =>
        statusUpdate.Type switch
        {
            StatusType.SequenceStart => [MoppyMessage.SysStart],
            StatusType.SequencePause => [MoppyMessage.SysStop],
            // If doReset, add reset message.
            StatusType.SequenceStopped or StatusType.SequenceEnd when statusUpdate.Data is true => [MoppyMessage.SysStop, MoppyMessage.SysReset],
            StatusType.SequenceStopped or StatusType.SequenceEnd => [MoppyMessage.SysStop],
            _ => [],
        };
}

/// <summary>
/// An ordered collection of EventMappers.  Every mapper sees every event.  Safe to modify while events are being mapped.
/// </summary>
/// <typeparam name="TInputEvent">Type of input event to be mapped to MoppyMessages.</typeparam>
public sealed class MapperCollection<TInputEvent>
{
    private readonly object mappersLock = new();
    private IEventMapper<TInputEvent>[] mappers = [];

    public void AddMapper(IEventMapper<TInputEvent> mapperToAdd)
    {
        ArgumentNullException.ThrowIfNull(mapperToAdd);
        lock (mappersLock)
        {
            mappers = [.. mappers, mapperToAdd];
        }
    }

    public void RemoveMapper(IEventMapper<TInputEvent> mapperToRemove)
    {
        lock (mappersLock)
        {
            int index = Array.IndexOf(mappers, mapperToRemove);
            if (index >= 0)
            {
                mappers = [.. mappers[..index], .. mappers[(index + 1)..]];
            }
        }
    }

    public void ClearMappers()
    {
        lock (mappersLock)
        {
            mappers = [];
        }
    }

    public IReadOnlyList<IEventMapper<TInputEvent>> Mappers => Volatile.Read(ref mappers);

    /// <summary>Maps an event with every mapper, in order, omitting mappers that don't handle it.</summary>
    public IReadOnlyList<MoppyMessage> MapEvent(TInputEvent inputEvent)
    {
        var results = new List<MoppyMessage>();
        foreach (var mapper in Volatile.Read(ref mappers))
        {
            var message = mapper.MapEvent(inputEvent);
            if (message is not null) // The mapper didn't know how to handle this event
            {
                results.Add(message);
            }
        }

        return results;
    }
}
