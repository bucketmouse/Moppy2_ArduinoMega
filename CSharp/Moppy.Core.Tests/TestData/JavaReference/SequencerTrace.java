import javax.sound.midi.*;
import java.util.*;

public class SequencerTrace {
    static final long T0 = System.nanoTime();
    static synchronized void log(String s) { System.out.printf("%5d %s%n", (System.nanoTime() - T0) / 1_000_000, s); }
    static String hex(byte[] b) { StringBuilder sb = new StringBuilder(); for (byte x : b) sb.append(String.format("%02X", x & 0xff)); return sb.toString(); }
    static ShortMessage sm(int cmd, int ch, int d1, int d2) throws Exception { return new ShortMessage(cmd, ch, d1, d2); }
    static MetaMessage tempo(int mpq) throws Exception { return new MetaMessage(0x51, new byte[]{(byte)(mpq>>16),(byte)(mpq>>8),(byte)mpq}, 3); }

    public static void main(String[] args) throws Exception {
        Sequence seq = new Sequence(Sequence.PPQ, 100);
        Track tr = seq.createTrack();
        tr.add(new MidiEvent(sm(ShortMessage.NOTE_ON, 0, 60, 100), 0));
        tr.add(new MidiEvent(tempo(250000), 20));
        tr.add(new MidiEvent(sm(ShortMessage.NOTE_ON, 1, 62, 100), 40));
        tr.add(new MidiEvent(sm(ShortMessage.NOTE_OFF, 0, 60, 0), 80));
        tr.add(new MidiEvent(sm(ShortMessage.NOTE_ON, 2, 64, 0), 90)); // zero-velocity note-on for a note that never started
        Track tr2 = seq.createTrack();
        tr2.add(new MidiEvent(new MetaMessage(0x2F, new byte[0], 0), 120)); // Longer second track: trailing silence

        Sequencer s = MidiSystem.getSequencer(false);
        s.open();
        s.getTransmitter().setReceiver(new Receiver() {
            public void send(MidiMessage m, long ts) { log("MSG " + hex(m.getMessage())); }
            public void close() {}
        });
        Object done = new Object();
        s.addMetaEventListener(meta -> { log("META " + meta.getType() + " running=" + s.isRunning() + " bpm=" + s.getTempoInBPM()); if (meta.getType() == 47) synchronized (done) { done.notifyAll(); } });
        s.setSequence(seq);
        log("loaded bpm=" + s.getTempoInBPM() + " lenUs=" + s.getMicrosecondLength());
        String mode = args.length > 0 ? args[0] : "end";
        s.start();
        if (mode.equals("stop")) {
            Thread.sleep(150); log("STOP"); s.stop(); log("stopped tick=" + s.getTickPosition()); Thread.sleep(100);
        } else if (mode.equals("seek")) {
            Thread.sleep(150); log("SEEK 100"); s.setTickPosition(100); log("after seek bpm=" + s.getTempoInBPM()); synchronized (done) { done.wait(5000); } Thread.sleep(50);
        } else {
            synchronized (done) { done.wait(5000); } Thread.sleep(50); log("after end running=" + s.isRunning() + " tick=" + s.getTickPosition());
        }
        s.close();
    }
}
