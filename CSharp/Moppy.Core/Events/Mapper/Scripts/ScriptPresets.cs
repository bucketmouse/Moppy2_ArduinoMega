namespace Moppy.Core.Events.Mapper.Scripts;

/// <summary>A named, built-in mapper script.</summary>
/// <param name="DisplayName">Name shown to users (and stored in configuration).</param>
/// <param name="Script">JavaScript expression evaluated by <see cref="MidiScriptMapper"/>.</param>
public sealed record ScriptPreset(string DisplayName, string Script);

/// <summary>Built-in condition scripts (must evaluate to a boolean).</summary>
public static class ConditionScripts
{
    public static readonly ScriptPreset AllEvents = new("All Events", "true");
    public static readonly ScriptPreset ChannelsOneToFour = new("Channels 1-4", "c>=1 && c<=4");
    public static readonly ScriptPreset OnlySupportedNotes = new("Only Supported Notes", "n>22 && n<67");

    public static IReadOnlyList<ScriptPreset> All { get; } = [AllEvents, ChannelsOneToFour, OnlySupportedNotes];
}

/// <summary>Built-in device address scripts (must evaluate to a byte).</summary>
public static class DeviceAddressScripts
{
    public static readonly ScriptPreset DeviceOne = new("Device 1", "1");
    public static readonly ScriptPreset OneDevicePerChannel = new("One Device per Channel", "c+1");

    public static IReadOnlyList<ScriptPreset> All { get; } = [DeviceOne, OneDevicePerChannel];
}

/// <summary>Built-in sub-address scripts (must evaluate to a byte).</summary>
public static class SubAddressScripts
{
    public static readonly ScriptPreset SubAddressPerChannel = new("Sub-address per Channel", "c+1");
    public static readonly ScriptPreset RoundRobin = new("Round Robin (8 drives)", "nextRoundRobinSubAddress(8)");

    public static IReadOnlyList<ScriptPreset> All { get; } = [SubAddressPerChannel, RoundRobin];
}

/// <summary>Built-in note scripts (must evaluate to a byte).</summary>
public static class NoteScripts
{
    public static readonly ScriptPreset StraightThrough = new("Straight Through", "n");
    public static readonly ScriptPreset ForceIntoRange = new("Force into Range", "((n-24)%48)+24");

    public static IReadOnlyList<ScriptPreset> All { get; } = [StraightThrough, ForceIntoRange];
}
