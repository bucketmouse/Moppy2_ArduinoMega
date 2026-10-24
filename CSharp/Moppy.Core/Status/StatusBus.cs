using Microsoft.Extensions.Logging;

namespace Moppy.Core.Status;

/// <summary>
/// Implemented by classes that want status updates from the sequencer or network bridges (e.g. to update a UI).
/// </summary>
public interface IStatusConsumer
{
    void ReceiveUpdate(StatusUpdate update);
}

/// <summary>
/// Acts as a bus to send StatusUpdates to multiple StatusConsumers.
/// </summary>
/// <remarks>
/// Consumers are called synchronously, in registration order, on the thread that posted the update.  If a
/// consumer posts another update while handling one, the new update is delivered after the current one has
/// reached every consumer, so all consumers observe updates in the same order.
/// </remarks>
public sealed class StatusBus : IStatusConsumer
{
    [ThreadStatic]
    private static Queue<(StatusBus Bus, StatusUpdate Update)>? pendingUpdates;

    private readonly object consumersLock = new();
    private IStatusConsumer[] consumers = [];

    public void RegisterConsumer(IStatusConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (consumersLock)
        {
            if (!consumers.Contains(consumer))
            {
                consumers = [.. consumers, consumer];
            }
        }
    }

    public void DeregisterConsumer(IStatusConsumer consumer)
    {
        lock (consumersLock)
        {
            consumers = consumers.Where(c => !ReferenceEquals(c, consumer)).ToArray();
        }
    }

    public void ReceiveUpdate(StatusUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (pendingUpdates is not null)
        {
            // Already delivering an update on this thread; finish that one first
            pendingUpdates.Enqueue((this, update));
            return;
        }

        pendingUpdates = new Queue<(StatusBus, StatusUpdate)>();
        try
        {
            Deliver(update);
            while (pendingUpdates.TryDequeue(out var pending))
            {
                pending.Bus.Deliver(pending.Update);
            }
        }
        finally
        {
            pendingUpdates = null;
        }
    }

    private void Deliver(StatusUpdate update)
    {
        foreach (var consumer in Volatile.Read(ref consumers))
        {
            try
            {
                consumer.ReceiveUpdate(update);
            }
            catch (Exception ex)
            {
                MoppyLogging.CreateLogger<StatusBus>().LogWarning(ex, "Status consumer {Consumer} failed handling {Update}", consumer, update);
            }
        }
    }
}
