using Moppy.Core.Comms;

namespace Moppy.Core.Events.PostProcessor;

/// <summary>
/// Transforms MoppyMessages after mapping (initially used for volume control).
/// </summary>
public interface IMessagePostProcessor
{
    MoppyMessage PostProcess(MoppyMessage message);

    /// <summary>Does no post-processing on messages.</summary>
    public static readonly IMessagePostProcessor PassThrough = new PassThroughPostProcessor();

    private sealed class PassThroughPostProcessor : IMessagePostProcessor
    {
        public MoppyMessage PostProcess(MoppyMessage message) => message;
    }
}
