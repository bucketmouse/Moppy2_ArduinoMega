# Java reference output

`*.txt` were produced by the original Java implementation (`ParityDump.java`, run against `Java/MoppyLib` and
`javax.sound.midi`) for each song in `Java/MoppyControlGUI/samplesongs`. Each line is:

    <track> <tick> <microseconds> <raw MIDI bytes> -> <MoppyMessage from MIDIEventMapper.defaultMapper(1)>

`ParityTests` checks that the C# MIDI file reader, tempo map, and mapper produce identical output. The one expected
difference is pitch bend values: Java's `defaultMapper` has an operator-precedence bug (`+` binds before `& 0xff`),
so the tests check pitch bends against the formula used by Java's `MIDIScriptMapper` (what the GUI uses) instead.

`SequencerTrace.java` records what the Java real-time sequencer sends when a sequence ends, is stopped, or is
repositioned; `MidiSequencer` follows the same behavior.

To regenerate (from this directory, with the Java sources listed in ParityDump's imports copied alongside):

    javac --add-exports java.desktop/com.sun.media.sound=ALL-UNNAMED -d classes *.java
    java --add-exports java.desktop/com.sun.media.sound=ALL-UNNAMED -cp classes ParityDump song.mid > song.txt
