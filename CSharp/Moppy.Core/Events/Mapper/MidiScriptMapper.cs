using Jint;
using Jint.Native;
using Microsoft.Extensions.Logging;
using Moppy.Core.Comms;
using Moppy.Core.Events.Mapper.Scripts;
using Moppy.Core.Midi;

namespace Moppy.Core.Events.Mapper;

/// <summary>
/// Thrown when a mapper script fails to compile, throws, or doesn't produce the expected kind of value.
/// </summary>
public class MapperScriptException : Exception
{
    public MapperScriptException(string message)
        : base(message)
    {
    }

    public MapperScriptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Maps MIDI events to MoppyMessages based on JavaScript expressions.
/// </summary>
/// <remarks>
/// <para>Four scripts determine how each note / pitch bend event is mapped:</para>
/// <list type="bullet">
/// <item><b>Condition</b> (boolean): whether this mapper handles the event at all.</item>
/// <item><b>Device address</b>, <b>sub-address</b> and <b>note</b> (numbers): where the event is sent, and which note is played.</item>
/// </list>
/// <para>Scripts can use the variables <c>c</c> (MIDI channel, 0-15), <c>n</c> (note number), <c>v</c> (velocity) and
/// <c>midiCommand</c> (e.g. 144 for note-on), and the function <c>nextRoundRobinSubAddress(numberOfSubAddresses)</c>.</para>
/// <para>Each mapper has its own script engine, and is safe to use from multiple threads.</para>
/// </remarks>
public sealed class MidiScriptMapper : MidiEventMapper
{
    /// <summary>Guards against runaway scripts (e.g. infinite loops) blocking playback.</summary>
    private const int MaxScriptStatements = 10_000;

    private readonly object sync = new();
    private readonly Engine engine;

    private readonly SortedDictionary<int, int> currentNotes = new(); // Sub-Address to Note map
    private int nextOpenRoundRobinSubAddress;

    private CompiledScript conditionScript = CompiledScript.Compile(ConditionScripts.AllEvents.Script);
    private CompiledScript deviceAddressScript = CompiledScript.Compile(DeviceAddressScripts.DeviceOne.Script);
    private CompiledScript subAddressScript = CompiledScript.Compile(SubAddressScripts.SubAddressPerChannel.Script);
    private CompiledScript noteScript = CompiledScript.Compile(NoteScripts.StraightThrough.Script);

    public MidiScriptMapper()
    {
        engine = new Engine(options => options
            .MaxStatements(MaxScriptStatements)
            .LimitRecursion(64)
            .Strict(false));

        SetDefaultBindings();

        // Set up utility functions
        engine.SetValue("nextRoundRobinSubAddress", new Func<int, int>(NextRoundRobinSubAddress));
    }

    private static ILogger Log => MoppyLogging.CreateLogger<MidiScriptMapper>();

    public string ConditionScript
    {
        get => conditionScript.Source;
        set => SetScript(ref conditionScript, value, ResultKind.Boolean);
    }

    public string DeviceAddressScript
    {
        get => deviceAddressScript.Source;
        set => SetScript(ref deviceAddressScript, value, ResultKind.Byte);
    }

    public string SubAddressScript
    {
        get => subAddressScript.Source;
        set => SetScript(ref subAddressScript, value, ResultKind.Byte);
    }

    public string NoteScript
    {
        get => noteScript.Source;
        set => SetScript(ref noteScript, value, ResultKind.Byte);
    }

    public override MoppyMessage? MapEvent(MidiMessage inputEvent)
    {
        switch (inputEvent)
        {
            case ShortMessage midiMessage:
                return MapShortMessage(midiMessage);
            case SysexMessage sysexMessage:
                return MapSysex(sysexMessage);
            default:
                return null; // We don't know how to handle this event
        }
    }

    private MoppyMessage? MapShortMessage(ShortMessage midiMessage)
    {
        lock (sync)
        {
            // Bind message variables
            engine.SetValue("c", midiMessage.Channel);
            engine.SetValue("midiCommand", midiMessage.Command);
            if (midiMessage.Command is ShortMessage.NoteOn or ShortMessage.NoteOff)
            {
                engine.SetValue("n", midiMessage.Data1);
                engine.SetValue("v", midiMessage.Data2);
            }

            try
            {
                if (!ResolveBoolean(conditionScript))
                {
                    return null; // If the condition doesn't match, just give up
                }

                switch (midiMessage.Command)
                {
                    case ShortMessage.NoteOn when midiMessage.Data2 == 0:
                        // For zero-velocity notes, turn the note off
                        return MoppyMessageFactory.DeviceStopNote(ResolveByte(deviceAddressScript), ResolveByte(subAddressScript), ResolveByte(noteScript));
                    case ShortMessage.NoteOn:
                        return MoppyMessageFactory.DevicePlayNote(ResolveByte(deviceAddressScript), ResolveByte(subAddressScript), ResolveByte(noteScript), (byte)midiMessage.Data2);
                    case ShortMessage.NoteOff:
                        return MoppyMessageFactory.DeviceStopNote(ResolveByte(deviceAddressScript), ResolveByte(subAddressScript), ResolveByte(noteScript));
                    case ShortMessage.PitchBend:
                        return MoppyMessageFactory.DevicePitchBend(ResolveByte(deviceAddressScript), ResolveByte(subAddressScript), PitchBendAmount(midiMessage));
                }
            }
            catch (MapperScriptException ex)
            {
                Log.LogWarning(ex, "Mapper script failed for {Message}", midiMessage);
            }

            return null;
        }
    }

    private bool ResolveBoolean(CompiledScript script) => ToBoolean(Evaluate(script), script.Source);

    private byte ResolveByte(CompiledScript script) => ToByte(Evaluate(script), script.Source);

    // Must hold sync
    private JsValue Evaluate(CompiledScript script)
    {
        try
        {
            return engine.Evaluate(script.Prepared);
        }
        catch (Exception ex) when (ex is not MapperScriptException and not OutOfMemoryException)
        {
            throw new MapperScriptException($"Error evaluating script '{script.Source}': {ex.Message}", ex);
        }
    }

    private static bool ToBoolean(JsValue value, string source) =>
        value.IsBoolean()
            ? value.AsBoolean()
            : throw new MapperScriptException($"Script '{source}' must evaluate to true or false, but returned {value}");

    private static byte ToByte(JsValue value, string source)
    {
        if (value.IsNumber())
        {
            double number = value.AsNumber();
            if (number == Math.Floor(number) && number is >= sbyte.MinValue and <= byte.MaxValue)
            {
                return unchecked((byte)(int)number);
            }
        }

        throw new MapperScriptException($"Script '{source}' must evaluate to a whole number from 0 to 255, but returned {value}");
    }

    /// <summary>
    /// Attempts to evaluate the new script with a range of test inputs before setting it.  Testing doesn't disturb
    /// the mapper's state.
    /// </summary>
    private void SetScript(ref CompiledScript target, string? source, ResultKind resultKind)
    {
        if (source is null)
        {
            throw new MapperScriptException("Script can't be null");
        }

        CompiledScript compiled;
        try
        {
            compiled = CompiledScript.Compile(source);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new MapperScriptException($"Script '{source}' could not be parsed: {ex.Message}", ex);
        }

        lock (sync)
        {
            var savedBindings = SaveBindings();
            var savedNotes = new SortedDictionary<int, int>(currentNotes);
            int savedRoundRobin = nextOpenRoundRobinSubAddress;
            try
            {
                foreach (var setBindings in new Action[] { SetDefaultBindings, SetLowBindings, SetHighBindings })
                {
                    setBindings();
                    var result = Evaluate(compiled);
                    if (resultKind == ResultKind.Boolean)
                    {
                        ToBoolean(result, source);
                    }
                    else
                    {
                        ToByte(result, source);
                    }
                }
            }
            finally
            {
                RestoreBindings(savedBindings);
                currentNotes.Clear();
                foreach (var (subAddress, note) in savedNotes)
                {
                    currentNotes[subAddress] = note;
                }

                nextOpenRoundRobinSubAddress = savedRoundRobin;
            }

            target = compiled;
        }
    }

    private void SetDefaultBindings()
    {
        // Set some sane defaults for testing
        engine.SetValue("c", 0);
        engine.SetValue("n", 60);
        engine.SetValue("v", 127);
        engine.SetValue("midiCommand", ShortMessage.NoteOn);
    }

    private void SetLowBindings()
    {
        engine.SetValue("c", 0);
        engine.SetValue("n", 0);
        engine.SetValue("v", 1);
    }

    private void SetHighBindings()
    {
        engine.SetValue("c", 15);
        engine.SetValue("n", 127);
        engine.SetValue("v", 127);
    }

    private static readonly string[] BindingNames = ["c", "n", "v", "midiCommand"];

    private JsValue[] SaveBindings() => BindingNames.Select(name => engine.GetValue(name)).ToArray();

    private void RestoreBindings(JsValue[] values)
    {
        for (int i = 0; i < BindingNames.Length; i++)
        {
            engine.SetValue(BindingNames[i], values[i]);
        }
    }

    ////
    // Utility functions accessible from scripts
    ////

    /// <summary>
    /// Assigns each new note to the next sub-address in rotation, and sends each note-off to the sub-address
    /// its note was assigned to.
    /// </summary>
    private int NextRoundRobinSubAddress(int numberOfChannels)
    {
        // Called from within a script, so sync is already held
        int incomingNote = (int)engine.GetValue("n").AsNumber(); // NOTE: Could be leftover binding from a previous message
        int midiCommand = (int)engine.GetValue("midiCommand").AsNumber();
        int velocity = (int)engine.GetValue("v").AsNumber();
        bool isNoteOn = midiCommand == ShortMessage.NoteOn && velocity > 0;
        bool isNoteOff = midiCommand == ShortMessage.NoteOff || (midiCommand == ShortMessage.NoteOn && velocity == 0);

        if (isNoteOn && numberOfChannels > 0)
        {
            // If this is a note on event, increment the sub address to the next available
            nextOpenRoundRobinSubAddress = nextOpenRoundRobinSubAddress % numberOfChannels + 1;
            // Remember the note so we know what to turn off the next time we get a note off event
            currentNotes[nextOpenRoundRobinSubAddress] = incomingNote;
            return nextOpenRoundRobinSubAddress;
        }

        if (isNoteOff)
        {
            // If we're currently playing this note somewhere, figure out where it is, and then remove it from the map
            foreach (var (subAddress, note) in currentNotes)
            {
                if (note == incomingNote)
                {
                    currentNotes.Remove(subAddress);
                    return subAddress;
                }
            }
        }

        return numberOfChannels + 1; // Push it outside the range (so hopefully it's ignored)
    }

    private enum ResultKind
    {
        Boolean,
        Byte,
    }

    private sealed record CompiledScript(string Source, Prepared<Acornima.Ast.Script> Prepared)
    {
        public static CompiledScript Compile(string source) => new(source, Engine.PrepareScript(source));
    }
}
