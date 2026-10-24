import com.moppy.core.comms.*;
import com.moppy.core.events.mapper.*;
import com.sun.media.sound.MidiUtils;
import javax.sound.midi.*;
import java.io.File;

public class ParityDump {
    static String hex(byte[] b) { StringBuilder sb = new StringBuilder(); for (byte x : b) sb.append(String.format("%02X", x & 0xff)); return sb.toString(); }
    public static void main(String[] args) throws Exception {
        Sequence seq = MidiSystem.getSequence(new File(args[0]));
        MidiUtils.TempoCache cache = new MidiUtils.TempoCache(seq);
        System.out.printf("division=%s resolution=%d tracks=%d ticks=%d micros=%d%n", seq.getDivisionType(), seq.getResolution(), seq.getTracks().length, seq.getTickLength(), seq.getMicrosecondLength());
        MIDIEventMapper mapper = MIDIEventMapper.defaultMapper((byte) 1);
        for (int t = 0; t < seq.getTracks().length; t++) {
            Track tr = seq.getTracks()[t];
            for (int i = 0; i < tr.size(); i++) {
                MidiEvent e = tr.get(i);
                MidiMessage m = e.getMessage();
                String mapped = "";
                if (!(m instanceof MetaMessage)) { MoppyMessage mm = mapper.mapEvent(m); if (mm != null) mapped = hex(mm.getMessageBytes()); }
                System.out.printf("%d %d %d %s -> %s%n", t, e.getTick(), MidiUtils.tick2microsecond(seq, e.getTick(), cache), hex(m.getMessage()), mapped);
            }
        }
    }
}
