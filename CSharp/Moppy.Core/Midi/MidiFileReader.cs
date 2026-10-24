using System.Buffers.Binary;

namespace Moppy.Core.Midi;

/// <summary>
/// Reads Standard MIDI Files (format 0, 1 and 2, optionally wrapped in a RIFF RMID container).
/// </summary>
/// <remarks>
/// The reader is deliberately lenient about common defects in real-world files: truncated tracks,
/// incorrect chunk lengths, missing End of Track events and running status after meta events are all tolerated.
/// </remarks>
public static class MidiFileReader
{
    public static MidiSequence Read(string path)
    {
        try
        {
            return Read(File.ReadAllBytes(path));
        }
        catch (InvalidMidiDataException ex)
        {
            throw new InvalidMidiDataException($"{Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }

    public static MidiSequence Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(buffer.ToArray());
    }

    /// <exception cref="InvalidMidiDataException">If the data isn't a readable MIDI file.</exception>
    public static MidiSequence Read(ReadOnlySpan<byte> data)
    {
        data = UnwrapRiff(data);

        if (data.Length < 14 || !data[..4].SequenceEqual("MThd"u8))
        {
            throw new InvalidMidiDataException("Not a standard MIDI file (missing MThd header)");
        }

        int headerLength = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[4..]), int.MaxValue);
        if (headerLength < 6)
        {
            throw new InvalidMidiDataException("MIDI file header is too short");
        }

        int format = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        int division = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
        if (format > 2)
        {
            throw new InvalidMidiDataException($"Unsupported MIDI file format {format}");
        }

        DivisionType divisionType;
        int resolution;
        if ((division & 0x8000) == 0)
        {
            divisionType = DivisionType.Ppq;
            resolution = division;
        }
        else
        {
            divisionType = (sbyte)(division >> 8) switch
            {
                -24 => DivisionType.Smpte24,
                -25 => DivisionType.Smpte25,
                -29 => DivisionType.Smpte30Drop,
                -30 => DivisionType.Smpte30,
                _ => throw new InvalidMidiDataException($"Unsupported SMPTE division 0x{division:X4}"),
            };
            resolution = division & 0xFF;
        }

        if (resolution == 0)
        {
            throw new InvalidMidiDataException("MIDI file has a timing resolution of 0");
        }

        var tracks = new List<MidiTrack>();
        long position = 8L + headerLength;
        while (position + 8 <= data.Length)
        {
            var chunkType = data.Slice((int)position, 4);
            long chunkLength = BinaryPrimitives.ReadUInt32BigEndian(data[(int)(position + 4)..]);
            long chunkStart = position + 8;
            long chunkEnd = Math.Min(chunkStart + chunkLength, data.Length);

            if (chunkType.SequenceEqual("MTrk"u8))
            {
                tracks.Add(ReadTrack(data[(int)chunkStart..(int)chunkEnd]));
            }
            // Unknown chunks are skipped

            position = chunkStart + chunkLength;
        }

        if (tracks.Count == 0)
        {
            throw new InvalidMidiDataException("MIDI file contains no tracks");
        }

        return new MidiSequence(divisionType, resolution, tracks);
    }

    /// <summary>Encodes a value as a MIDI variable-length quantity.</summary>
    public static byte[] EncodeVariableLength(long value)
    {
        if (value is < 0 or > 0x0FFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Span<byte> buffer = stackalloc byte[4];
        int index = 3;
        buffer[index] = (byte)(value & 0x7F);
        while ((value >>= 7) > 0)
        {
            buffer[--index] = (byte)((value & 0x7F) | 0x80);
        }

        return buffer[index..].ToArray();
    }

    private static ReadOnlySpan<byte> UnwrapRiff(ReadOnlySpan<byte> data)
    {
        // RIFF <size> RMID ... "data" <size> <SMF>
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("RMID"u8))
        {
            return data;
        }

        int position = 12;
        while (position + 8 <= data.Length)
        {
            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]), int.MaxValue);
            if (data.Slice(position, 4).SequenceEqual("data"u8))
            {
                return data.Slice(position + 8, Math.Min(length, data.Length - position - 8));
            }

            position += 8 + length + (length & 1);
        }

        throw new InvalidMidiDataException("RMID file contains no MIDI data");
    }

    private static MidiTrack ReadTrack(ReadOnlySpan<byte> track)
    {
        var events = new List<MidiEvent>();
        int pos = 0;
        long tick = 0;
        int runningStatus = 0;

        while (pos < track.Length)
        {
            if (!TryReadVariableLength(track, ref pos, out long delta))
            {
                break; // Truncated
            }

            tick += delta;
            if (pos >= track.Length)
            {
                break;
            }

            int status = track[pos];
            if (status < 0x80)
            {
                if (runningStatus == 0)
                {
                    break; // Data byte with no running status; the rest of the track is unreadable
                }

                status = runningStatus; // Running status: this byte is the first data byte
            }
            else
            {
                pos++;
            }

            if (status < 0xF0)
            {
                runningStatus = status;
                int dataLength = ShortMessage.GetDataLength(status);
                if (pos + dataLength > track.Length)
                {
                    break;
                }

                var bytes = new byte[1 + dataLength];
                bytes[0] = (byte)status;
                track.Slice(pos, dataLength).CopyTo(bytes.AsSpan(1));
                pos += dataLength;
                if (bytes.AsSpan(1).IndexOfAnyInRange((byte)0x80, (byte)0xFF) >= 0)
                {
                    continue; // Corrupt data byte; skip the event
                }

                events.Add(new MidiEvent(tick, ShortMessage.FromValidatedBytes(bytes)));
            }
            else if (status is SysexMessage.SystemExclusive or SysexMessage.SpecialSystemExclusive)
            {
                if (!TryReadVariableLength(track, ref pos, out long length) || pos + length > track.Length)
                {
                    break;
                }

                events.Add(new MidiEvent(tick, new SysexMessage(status, track.Slice(pos, (int)length))));
                pos += (int)length;
            }
            else if (status == MetaMessage.Meta)
            {
                if (pos >= track.Length)
                {
                    break;
                }

                int type = track[pos++];
                if (!TryReadVariableLength(track, ref pos, out long length) || pos + length > track.Length)
                {
                    break;
                }

                if (type == MetaMessage.EndOfTrackType)
                {
                    events.Add(new MidiEvent(tick, MetaMessage.EndOfTrack));
                    break;
                }

                if (type < 0x80)
                {
                    events.Add(new MidiEvent(tick, new MetaMessage(type, track.Slice(pos, (int)length))));
                }

                pos += (int)length;
            }
            else
            {
                break; // System common / real-time messages aren't valid in files
            }
        }

        return new MidiTrack(events);
    }

    private static bool TryReadVariableLength(ReadOnlySpan<byte> data, ref int pos, out long value)
    {
        value = 0;
        for (int i = 0; i < 4; i++)
        {
            if (pos >= data.Length)
            {
                return false;
            }

            byte b = data[pos++];
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false; // More than 4 bytes isn't a valid variable-length quantity
    }
}
