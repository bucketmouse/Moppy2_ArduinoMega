# Moppy for .NET

A C# port of the Java Moppy control software in [`../Java`](../Java): a library (`Moppy.Core`) that does everything
the Java application does, and a desktop app (`Moppy.Control`) that replicates the Java *Moppy Control* GUI on top
of it.

| Project | What it is |
| --- | --- |
| `Moppy.Core` | Library: MIDI file playback, mapping MIDI to Moppy messages, serial / UDP network bridges, device discovery, playlist, configuration. Port of `MoppyLib` plus the application logic from `MoppyControlGUI`. |
| `Moppy.Control` | Cross-platform desktop app (Avalonia) with the same panels as the Java GUI. |
| `Moppy.Core.Tests` | Library tests, including parity tests against output recorded from the Java implementation. |
| `Moppy.Control.Tests` | Headless UI tests that drive the real main window. |

## Building and running

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
cd CSharp
dotnet run --project Moppy.Control                  # run the app
dotnet run --project Moppy.Control -- song.mid      # ...with files already in the playlist
dotnet test                                          # run all tests
dotnet publish Moppy.Control -c Release -r win-x64   # or osx-arm64, linux-x64, ...
```

The app works like the Java one: tick a network bridge (the UDP multicast group or a serial port, with its baud rate)
to connect, and devices that answer pings appear on the right. Add MIDI files to the playlist and press play.
Mappers (bottom half) decide which device, drive, and note each MIDI event is sent to.

Settings (window layout, mappers, auto-reset, last folder) are saved to `Moppy/moppy-config.json` in the user's
application data folder (`%APPDATA%` on Windows), with a log file (`moppy_control.log`) alongside.
Use `--config <path>` to use a different file.

MIDI In / MIDI Out devices are supported on Windows and macOS (via
[DryWetMidi](https://github.com/melanchall/drywetmidi)); on Linux those menus only offer "None".

## Using the library

`MoppyController` wires everything together the same way the Java app's `main` did:

```csharp
using Moppy.Core.Control;

using var controller = new MoppyController(MoppyPreferences.Load());
controller.Start();                                    // start pinging for devices
controller.NetworkManager.ConnectBridge("COM3", 57600);
// or: controller.NetworkManager.ConnectBridge("239.2.2.7:30994", null);

controller.Playlist.Add("Tetris.mid");
controller.Playlist.TogglePlayPause();                 // play
controller.Sequencer.SetTempo(140);
controller.PostProcessor.VelocityMultiplier = 0.5;     // 50% volume

var mapper = controller.AddMapper();
mapper.SelectChoice(MapperField.SubAddress, "Round Robin (8 drives)");
mapper.SelectChoice(MapperField.Note, MapperPresets.CustomChoice);
mapper.SetCustomScript(MapperField.Note, "n + 12");    // returns false (see GetError) if invalid
```

Register an `IStatusConsumer` on `controller.StatusBus` to follow playback and network changes, and set
`MoppyLogging.LoggerFactory` to see the library's log output. The pieces can also be used on their own, e.g.
`BridgeSerial` / `BridgeUdp` to talk to devices directly, `MidiFileReader` + `MidiSequencer` to play MIDI anywhere,
or `MoppyDevice` to implement a (simulated) device.

### Where the Java classes went

| Java | C# |
| --- | --- |
| `comms.MoppyMessage`, `MoppyMessageFactory`, `NetworkReceivedMessage`, `NetworkMessageConsumer` | `Moppy.Core.Comms` (command bytes are in `CommandByte`; consumer is `INetworkMessageConsumer`) |
| `comms.bridge.*` (`NetworkBridge`, `BridgeSerial`, `BridgeUDP`, `MultiBridge`) | `Moppy.Core.Comms.Bridge` (`BridgeUdp`) |
| `device.DeviceDescriptor`, `MoppyDevice` | `Moppy.Core.Device` |
| `events.mapper.*`, `events.mapper.scripts.*` | `Moppy.Core.Events.Mapper` (script presets are `ScriptPreset` records in `ConditionScripts` etc.) |
| `events.postprocessor.MessagePostProcessor` | `Moppy.Core.Events.PostProcessor.IMessagePostProcessor` |
| `midi.MoppyMIDISequencer`, `MoppyMIDIReceiverSender` | `Moppy.Core.Midi.MoppyMidiSequencer`, `MoppyMidiReceiverSender` |
| `javax.sound.midi` (messages, files, `Sequencer`, devices) | `Moppy.Core.Midi`: `MidiMessage` / `ShortMessage` / `SysexMessage` / `MetaMessage`, `MidiFileReader`, `MidiSequence`, `MidiSequencer`, `MidiDevices` |
| `status.*` | `Moppy.Core.Status` (`IStatusConsumer`) |
| `util.Notes` | `Moppy.Core.Util.Notes` |
| GUI app: `NetworkManager`, `GUIControlledPostProcessor`, `MoppyConfig`, `MoppyPreferences` | `Moppy.Core.Control`: `NetworkManager`, `VelocityPostProcessor`, `MoppyConfig`, `MoppyPreferences` |
| GUI panels' logic: playlist (`SequencerPanel`), preset/custom script handling (`MapperPanel`), wiring (`MoppyControlGUI`) | `Moppy.Core.Control`: `Playlist`, `ConfiguredMapper` / `MapperPresets`, `MoppyController` |
| GUI panels | `Moppy.Control/Views` |

## Differences from the Java version

Behavior, the network protocol, and the saved choices match the Java version. Settings are stored in a JSON file
rather than Java preferences, so they aren't carried over. Things that work differently:

**Bugs fixed**

- Round-robin sub-addressing treated a note-on with velocity 0 (a common way of writing note-off) as a new note, so
  it silenced the wrong drive and left the real one playing. It's now treated as a note-off.
- All Java mappers shared one global script context, so with several round-robin mappers they all used the last
  mapper's state. Each mapper now has its own.
- Checking a script (when it's chosen or edited) ran it with test inputs, which advanced the round-robin state and
  left stale notes in it. Checking no longer changes the mapper's state.
- `MIDIEventMapper.defaultMapper` computed pitch bends wrongly (operator precedence); it now matches the script mapper.
- `MoppyDevice` treated `0x82` as system reset; the firmware uses `0xFF`. Pitch-bend payloads with a low byte ≥ 0x80
  were also decoded wrongly.
- Tempo changes in a file were truncated to whole BPM (e.g. 92.5 BPM played at 92); they're now exact.
- `SYS_START` is sent before the first note of a song rather than racing it.
- Errors from a mapper's script were shown on the Note box regardless of which script failed.
- The serial listener assumed a message body always arrives in one read.

**Small additions**

- A refresh button rescans serial ports (Java only looked at startup), and connection errors are shown in the
  Network panel instead of only being logged.
- Custom scripts are limited to 10,000 statements per evaluation, so a runaway script (e.g. an infinite loop) is
  rejected instead of freezing playback. Script errors are shown as a tooltip on the red script box.
- Mapper choices are listed in a fixed order (Java's order was arbitrary); playlist files can be added several at
  a time, and `.midi` files are accepted too.
- Serial ports that don't support DTR/RTS (some virtual / Bluetooth ports) can still be opened.
