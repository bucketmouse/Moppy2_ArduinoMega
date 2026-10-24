using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Moppy.Core.Events.Mapper.Scripts;

namespace Moppy.Core.Control;

/// <summary>
/// Configuration options for the controller, in a JSON-serializable format.
/// </summary>
public sealed class MoppyConfig
{
    // Sequencer
    public string FileLoadDirectory { get; set; } = ".";
    public bool AutoReset { get; set; }
    public WindowSize MainWindowSize { get; set; } = new(1024, 600);
    public int MainWindowDividerPosition { get; set; } = 300;

    // Mappers (a single default mapper unless the user has saved their own)
    public List<MidiScriptMapperConfig> MapperConfigs { get; set; } = [new()];
}

public sealed record WindowSize(int Width, int Height);

/// <summary>
/// Saved state of a <see cref="Events.Mapper.MidiScriptMapper"/>: for each script, the chosen preset (by display
/// name, or "Custom") and the user's custom script.
/// </summary>
public sealed class MidiScriptMapperConfig
{
    public string ConditionChoice { get; set; } = ConditionScripts.AllEvents.DisplayName;
    public string ConditionCustomScript { get; set; } = ConditionScripts.AllEvents.Script;
    public string DeviceAddressChoice { get; set; } = DeviceAddressScripts.DeviceOne.DisplayName;
    public string DeviceAddressCustomScript { get; set; } = DeviceAddressScripts.DeviceOne.Script;
    public string SubAddressChoice { get; set; } = SubAddressScripts.SubAddressPerChannel.DisplayName;
    public string SubAddressCustomScript { get; set; } = SubAddressScripts.SubAddressPerChannel.Script;
    public string NoteChoice { get; set; } = NoteScripts.StraightThrough.DisplayName;
    public string NoteCustomScript { get; set; } = NoteScripts.StraightThrough.Script;

    public MidiScriptMapperConfig Clone() => (MidiScriptMapperConfig)MemberwiseClone();
}

/// <summary>
/// Loads and saves <see cref="MoppyConfig"/> as a JSON file.
/// </summary>
public sealed class MoppyPreferences
{
    private readonly object saveLock = new();

    private MoppyPreferences(string configPath, MoppyConfig configuration)
    {
        ConfigPath = configPath;
        Configuration = configuration;
    }

    /// <summary>Default location: <c>Moppy/moppy-config.json</c> in the user's application data directory.</summary>
    public static string DefaultConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Moppy", "moppy-config.json");

    public string ConfigPath { get; }

    public MoppyConfig Configuration { get; }

    private static ILogger Log => MoppyLogging.CreateLogger<MoppyPreferences>();

    /// <summary>Loads the configuration, falling back to defaults if the file is missing or unreadable.</summary>
    public static MoppyPreferences Load(string? configPath = null)
    {
        configPath ??= DefaultConfigPath;
        MoppyConfig config;
        try
        {
            config = File.Exists(configPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(configPath), MoppyConfigJsonContext.Default.MoppyConfig) ?? new MoppyConfig()
                : new MoppyConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Failed to parse configuration-- using default");
            config = new MoppyConfig();
        }

        return new MoppyPreferences(configPath, config);
    }

    /// <summary>Creates preferences that are never written to disk (e.g. for tests).</summary>
    public static MoppyPreferences InMemory(MoppyConfig? configuration = null) =>
        new(string.Empty, configuration ?? new MoppyConfig());

    public void Save()
    {
        if (string.IsNullOrEmpty(ConfigPath))
        {
            return;
        }

        lock (saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ConfigPath))!);
                string json = JsonSerializer.Serialize(Configuration, MoppyConfigJsonContext.Default.MoppyConfig);
                string tempPath = ConfigPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, ConfigPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.LogWarning(ex, "Failed to save preferences!");
            }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(MoppyConfig))]
internal sealed partial class MoppyConfigJsonContext : JsonSerializerContext;
