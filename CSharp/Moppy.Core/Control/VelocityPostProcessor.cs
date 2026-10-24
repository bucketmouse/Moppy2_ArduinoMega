using Moppy.Core.Comms;
using Moppy.Core.Events.PostProcessor;

namespace Moppy.Core.Control;

/// <summary>
/// Post-processor implementing the controller's volume settings by adjusting the velocity of play-note messages.
/// </summary>
public sealed class VelocityPostProcessor : IMessagePostProcessor
{
    private volatile bool overrideVelocity;
    private double velocityMultiplier = 1;

    /// <summary>
    /// If true, all velocities are set uniformly to 127 × <see cref="VelocityMultiplier"/>; if false, velocities
    /// are scaled by the multiplier.
    /// </summary>
    public bool OverrideVelocity
    {
        get => overrideVelocity;
        set => overrideVelocity = value;
    }

    /// <summary>Volume multiplier (1 = 100%).</summary>
    public double VelocityMultiplier
    {
        get => Volatile.Read(ref velocityMultiplier);
        set => Volatile.Write(ref velocityMultiplier, value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    public MoppyMessage PostProcess(MoppyMessage message)
    {
        if (message.IsSystemMessage || message.Length < 7 || message.MessageBody.Length < 3 ||
            message.MessageCommandByte != CommandByte.DevPlayNote)
        {
            return message;
        }

        var bytes = message.ToArray();
        double velocity = overrideVelocity ? 127 * VelocityMultiplier : bytes[6] * VelocityMultiplier;
        bytes[6] = (byte)Math.Clamp((int)velocity, 0, 127);
        return MoppyMessageFactory.FromBytes(bytes);
    }
}
