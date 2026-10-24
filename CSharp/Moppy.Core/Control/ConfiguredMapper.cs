using Moppy.Core.Events.Mapper;
using Moppy.Core.Events.Mapper.Scripts;

namespace Moppy.Core.Control;

/// <summary>The four scripts of a <see cref="MidiScriptMapper"/>.</summary>
public enum MapperField
{
    Condition,
    DeviceAddress,
    SubAddress,
    Note,
}

/// <summary>Built-in script choices for each <see cref="MapperField"/>.</summary>
public static class MapperPresets
{
    /// <summary>The choice that uses the user's own script instead of a preset.</summary>
    public const string CustomChoice = "Custom";

    public static IReadOnlyList<ScriptPreset> For(MapperField field) => field switch
    {
        MapperField.Condition => ConditionScripts.All,
        MapperField.DeviceAddress => DeviceAddressScripts.All,
        MapperField.SubAddress => SubAddressScripts.All,
        MapperField.Note => NoteScripts.All,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    /// <summary>Every choice for a field: the preset display names followed by <see cref="CustomChoice"/>.</summary>
    public static IReadOnlyList<string> ChoicesFor(MapperField field) =>
        [.. For(field).Select(p => p.DisplayName), CustomChoice];
}

/// <summary>
/// A <see cref="MidiScriptMapper"/> paired with its saved <see cref="MidiScriptMapperConfig"/>.  For each script, either
/// a preset or a custom script can be chosen; choices are applied to the mapper and recorded in the config.
/// </summary>
public sealed class ConfiguredMapper
{
    private readonly object sync = new();
    private readonly Dictionary<MapperField, string?> errors = new();

    /// <summary>Creates a mapper from a config, applying each saved choice.  Invalid scripts are reported via <see cref="GetError"/>.</summary>
    public ConfiguredMapper(MidiScriptMapperConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        foreach (var field in Enum.GetValues<MapperField>())
        {
            string choice = GetChoice(field);
            if (!MapperPresets.ChoicesFor(field).Contains(choice))
            {
                choice = MapperPresets.For(field)[0].DisplayName; // Unknown preset; fall back to the default
            }

            SelectChoice(field, choice);
        }
    }

    public MidiScriptMapper Mapper { get; } = new();

    /// <summary>The live config object (updated as choices and custom scripts change).</summary>
    public MidiScriptMapperConfig Config { get; }

    /// <summary>Raised whenever a choice, script, or error changes.</summary>
    public event EventHandler? Changed;

    public string GetChoice(MapperField field) => field switch
    {
        MapperField.Condition => Config.ConditionChoice,
        MapperField.DeviceAddress => Config.DeviceAddressChoice,
        MapperField.SubAddress => Config.SubAddressChoice,
        MapperField.Note => Config.NoteChoice,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    public bool IsCustom(MapperField field) => GetChoice(field) == MapperPresets.CustomChoice;

    /// <summary>The user's saved custom script (used when the choice is <see cref="MapperPresets.CustomChoice"/>).</summary>
    public string GetCustomScript(MapperField field) => field switch
    {
        MapperField.Condition => Config.ConditionCustomScript,
        MapperField.DeviceAddress => Config.DeviceAddressCustomScript,
        MapperField.SubAddress => Config.SubAddressCustomScript,
        MapperField.Note => Config.NoteCustomScript,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    /// <summary>The script text for the current choice (the preset's script, or the custom script).</summary>
    public string GetChoiceScript(MapperField field) =>
        IsCustom(field)
            ? GetCustomScript(field)
            : MapperPresets.For(field).First(p => p.DisplayName == GetChoice(field)).Script;

    /// <summary>The script the mapper is actually using.</summary>
    public string GetActiveScript(MapperField field) => field switch
    {
        MapperField.Condition => Mapper.ConditionScript,
        MapperField.DeviceAddress => Mapper.DeviceAddressScript,
        MapperField.SubAddress => Mapper.SubAddressScript,
        MapperField.Note => Mapper.NoteScript,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    /// <summary>The error from the last attempt to apply a script to this field, or null if it succeeded.</summary>
    public string? GetError(MapperField field)
    {
        lock (sync)
        {
            return errors.GetValueOrDefault(field);
        }
    }

    /// <summary>
    /// Chooses a preset (by display name) or <see cref="MapperPresets.CustomChoice"/>, and applies its script to the mapper.
    /// </summary>
    /// <returns>False if the script was invalid (the mapper keeps its previous script; see <see cref="GetError"/>).</returns>
    public bool SelectChoice(MapperField field, string choice)
    {
        if (!MapperPresets.ChoicesFor(field).Contains(choice))
        {
            throw new ArgumentException($"'{choice}' is not a choice for {field}", nameof(choice));
        }

        lock (sync)
        {
            SetChoice(field, choice);
            return Apply(field, GetChoiceScript(field), saveAsCustom: false);
        }
    }

    /// <summary>
    /// Validates a custom script and, if valid, applies it to the mapper and saves it in the config.
    /// Only applies when the field's choice is <see cref="MapperPresets.CustomChoice"/>.
    /// </summary>
    /// <returns>False if the script was invalid (see <see cref="GetError"/>).</returns>
    public bool SetCustomScript(MapperField field, string script)
    {
        lock (sync)
        {
            if (!IsCustom(field))
            {
                throw new InvalidOperationException($"{field} is not using a custom script");
            }

            return Apply(field, script, saveAsCustom: true);
        }
    }

    // Must hold sync
    private bool Apply(MapperField field, string script, bool saveAsCustom)
    {
        bool success;
        try
        {
            switch (field)
            {
                case MapperField.Condition:
                    Mapper.ConditionScript = script;
                    break;
                case MapperField.DeviceAddress:
                    Mapper.DeviceAddressScript = script;
                    break;
                case MapperField.SubAddress:
                    Mapper.SubAddressScript = script;
                    break;
                case MapperField.Note:
                    Mapper.NoteScript = script;
                    break;
            }

            if (saveAsCustom)
            {
                SetCustomScriptInConfig(field, script);
            }

            errors[field] = null;
            success = true;
        }
        catch (MapperScriptException ex)
        {
            errors[field] = ex.Message;
            success = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return success;
    }

    private void SetChoice(MapperField field, string choice)
    {
        switch (field)
        {
            case MapperField.Condition: Config.ConditionChoice = choice; break;
            case MapperField.DeviceAddress: Config.DeviceAddressChoice = choice; break;
            case MapperField.SubAddress: Config.SubAddressChoice = choice; break;
            case MapperField.Note: Config.NoteChoice = choice; break;
        }
    }

    private void SetCustomScriptInConfig(MapperField field, string script)
    {
        switch (field)
        {
            case MapperField.Condition: Config.ConditionCustomScript = script; break;
            case MapperField.DeviceAddress: Config.DeviceAddressCustomScript = script; break;
            case MapperField.SubAddress: Config.SubAddressCustomScript = script; break;
            case MapperField.Note: Config.NoteCustomScript = script; break;
        }
    }
}
