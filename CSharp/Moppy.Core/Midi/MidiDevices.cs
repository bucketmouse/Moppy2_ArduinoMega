using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Microsoft.Extensions.Logging;

namespace Moppy.Core.Midi;

/// <summary>Thrown when a MIDI device can't be opened or used.</summary>
public class MidiUnavailableException : Exception
{
    public MidiUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Access to the system's MIDI In and MIDI Out devices.
/// </summary>
/// <remarks>
/// Device access is supported on Windows and macOS.  On other platforms (or if the system's MIDI service fails)
/// no devices are listed, so MIDI In/Out is simply unavailable.
/// </remarks>
public static class MidiDevices
{
    private static ILogger Log => MoppyLogging.CreateLogger(typeof(MidiDevices));

    /// <summary>Names of devices that MIDI events can be received from.</summary>
    public static IReadOnlyList<string> GetInputDeviceNames() =>
        GetNames(() => InputDevice.GetAll().Select(d => Release(d, d.Name)));

    /// <summary>Names of devices that MIDI events can be sent to.</summary>
    public static IReadOnlyList<string> GetOutputDeviceNames() =>
        GetNames(() => OutputDevice.GetAll().Select(d => Release(d, d.Name)));

    /// <summary>Opens a MIDI In device, forwarding everything it receives to <paramref name="receiver"/>.</summary>
    /// <exception cref="MidiUnavailableException">If the device can't be opened.</exception>
    public static MidiInputConnection OpenInput(string deviceName, IMidiReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        try
        {
            var device = InputDevice.GetByName(deviceName);
            return new MidiInputConnection(device, receiver);
        }
        catch (Exception ex) when (ex is not MidiUnavailableException)
        {
            throw new MidiUnavailableException($"Unable to open MIDI In device '{deviceName}': {ex.Message}", ex);
        }
    }

    /// <summary>Opens a MIDI Out device as a receiver of MIDI messages.</summary>
    /// <exception cref="MidiUnavailableException">If the device can't be opened.</exception>
    public static MidiOutputReceiver OpenOutput(string deviceName)
    {
        try
        {
            var device = OutputDevice.GetByName(deviceName);
            device.PrepareForEventsSending();
            return new MidiOutputReceiver(device);
        }
        catch (Exception ex) when (ex is not MidiUnavailableException)
        {
            throw new MidiUnavailableException($"Unable to open MIDI Out device '{deviceName}': {ex.Message}", ex);
        }
    }

    private static string Release(IDisposable device, string name)
    {
        device.Dispose();
        return name;
    }

    /// <summary>Whether MIDI device access is supported on this platform.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    private static IReadOnlyList<string> GetNames(Func<IEnumerable<string>> getNames)
    {
        if (!IsSupported)
        {
            Log.LogInformation("MIDI devices aren't supported on this platform-- MIDI In/Out will be unavailable");
            return [];
        }

        try
        {
            // Duplicate names can't be told apart (the Java version had the same limitation)
            return getNames().Distinct().ToArray();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Exception getting list of MIDI devices-- MIDI In/Out will be unavailable");
            return [];
        }
    }
}

/// <summary>
/// An open MIDI In device.  Dispose to close it.
/// </summary>
public sealed class MidiInputConnection : IDisposable
{
    private readonly InputDevice device;
    private readonly IMidiReceiver receiver;
    private readonly MidiEventToBytesConverter converter = new();
    private readonly object converterLock = new();

    internal MidiInputConnection(InputDevice device, IMidiReceiver receiver)
    {
        this.device = device;
        this.receiver = receiver;
        device.SilentNoteOnPolicy = SilentNoteOnPolicy.NoteOn; // Pass zero-velocity note-ons through unchanged
        device.EventReceived += OnEventReceived;
        device.StartEventsListening();
    }

    public string Name => device.Name;

    private void OnEventReceived(object? sender, MidiEventReceivedEventArgs e)
    {
        try
        {
            MidiMessage message;
            switch (e.Event)
            {
                case NormalSysExEvent sysex:
                    message = new SysexMessage(SysexMessage.SystemExclusive, sysex.Data ?? []);
                    break;
                case EscapeSysExEvent escape:
                    message = new SysexMessage(SysexMessage.SpecialSystemExclusive, escape.Data ?? []);
                    break;
                default:
                    byte[] bytes;
                    lock (converterLock)
                    {
                        bytes = converter.Convert(e.Event);
                    }

                    message = MidiMessage.FromBytes(bytes);
                    break;
            }

            receiver.Send(message, -1);
        }
        catch (Exception ex)
        {
            MoppyLogging.CreateLogger<MidiInputConnection>().LogWarning(ex, "Failed to handle MIDI In event {Event}", e.Event);
        }
    }

    public void Dispose()
    {
        device.EventReceived -= OnEventReceived;
        try
        {
            device.StopEventsListening();
        }
        finally
        {
            device.Dispose();
            converter.Dispose();
        }
    }
}

/// <summary>
/// An open MIDI Out device that sends the MIDI messages it receives.  Dispose to close it.
/// </summary>
public sealed class MidiOutputReceiver : IMidiReceiver, IDisposable
{
    private readonly OutputDevice device;
    private readonly BytesToMidiEventConverter converter = new();
    private readonly object sendLock = new();

    internal MidiOutputReceiver(OutputDevice device)
    {
        this.device = device;
        converter.SilentNoteOnPolicy = SilentNoteOnPolicy.NoteOn;
    }

    public string Name => device.Name;

    public void Send(MidiMessage message, long timeStamp)
    {
        lock (sendLock)
        {
            Melanchall.DryWetMidi.Core.MidiEvent midiEvent = message switch
            {
                SysexMessage { Status: SysexMessage.SystemExclusive } sysex => new NormalSysExEvent(sysex.Data.ToArray()),
                SysexMessage sysex => new EscapeSysExEvent(sysex.Data.ToArray()),
                MetaMessage => throw new ArgumentException("Meta messages can't be sent to MIDI devices", nameof(message)),
                _ => converter.Convert(message.ToArray()),
            };
            device.SendEvent(midiEvent);
        }
    }

    public void Dispose()
    {
        lock (sendLock)
        {
            try
            {
                device.TurnAllNotesOff();
            }
            catch (Exception ex)
            {
                MoppyLogging.CreateLogger<MidiOutputReceiver>().LogDebug(ex, "Unable to turn notes off on {Device}", device.Name);
            }

            device.Dispose();
            converter.Dispose();
        }
    }
}
