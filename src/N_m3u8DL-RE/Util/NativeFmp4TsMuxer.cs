using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using N_m3u8DL_RE.Common.Log;
using Spectre.Console;

namespace N_m3u8DL_RE.Util;

/// <summary>
/// Small FFmpeg-free live fMP4 -> MPEG-TS muxer used by the StreamRelay pipe path.
/// It is intentionally limited to one H.264/H.265 video track plus one AAC audio track.
/// The output is written directly to a supplied streaming sink; no growing recording
/// file is created by this class.
/// </summary>
internal sealed class NativeFmp4TsMuxer
{
    private const int VideoPid = 0x100;
    private const int AudioPid = 0x101;
    private const int PmtPid = 0x1000;
    private const int PcrPid = VideoPid;

    private readonly Stream _output;
    private readonly object _writeLock = new();
    private Track? _video;
    private Track? _audio;
    private bool _tablesWritten;
    private bool _firstVideo = true;
    private long _lastPcr = -1;

    private NativeFmp4TsMuxer(Stream output) => _output = output;

    public static async Task<bool> RunAsync(string[] pipeNames, string outputPath)
    {
        if (pipeNames.Length != 2)
        {
            Logger.ErrorMarkUp("[red]Native fMP4 mux requires exactly one video and one audio pipe.[/]");
            return false;
        }

        try
        {
            var streams = pipeNames.Select(OpenPipe).ToArray();
            await using var a = streams[0];
            await using var b = streams[1];
            await using var output = new FileStream(outputPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            output.SetLength(0);

            var muxer = new NativeFmp4TsMuxer(output);
            var tasks = streams.Select((s, i) => muxer.ReadTrackAsync(s, i)).ToArray();
            await Task.WhenAll(tasks);
            await output.FlushAsync();
            return muxer._tablesWritten;
        }
        catch (Exception ex)
        {
            Logger.ErrorMarkUp($"[red]Native fMP4 mux failed: {ex.Message.EscapeMarkup()}[/]");
            return false;
        }
    }

    private static Stream OpenPipe(string name)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream($"\\\\.\\pipe\\{name}", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

        return new FileStream(Path.Combine(Path.GetTempPath(), name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private async Task ReadTrackAsync(Stream pipe, int pipeIndex)
    {
        var parser = new FragmentReader(pipe);
        var track = new Track();
        var fragments = new List<Fragment>();

        while (true)
        {
            var box = await parser.ReadBoxAsync();
            if (box == null) break;

            switch (box.Type)
            {
                case "ftyp":
                case "styp":
                    break;
                case "moov":
                    ParseInit(box.Payload, track);
                    break;
                case "moof":
                    var next = await parser.ReadBoxAsync();
                    if (next == null || next.Type != "mdat")
                        throw new InvalidDataException("fMP4 fragment is missing mdat after moof");
                    ParseFragment(box.Payload, next.Payload, track, fragments);
                    break;
                case "mdat":
                    break;
            }
        }

        if (track.Kind == TrackKind.Unknown)
            throw new InvalidDataException($"Could not identify pipe {pipeIndex} as audio or video");

        if (track.Kind == TrackKind.Video)
            _video = track;
        else
            _audio = track;

        // Fragments are retained only for the duration of this live process. In normal
        // operation this list stays bounded by the amount read before the other pipe
        // reaches EOF. We emit as soon as both track descriptions are known.
        foreach (var fragment in fragments)
            await EmitFragmentAsync(track, fragment);
    }

    private void ParseInit(byte[] moov, Track track)
    {
        foreach (var box in Boxes(moov))
        {
            if (box.Type != "trak") continue;
            var handler = FindBytes(box.Payload, "hdlr");
            if (handler >= 0 && handler + 12 <= box.Payload.Length)
            {
                var handlerType = Encoding.ASCII.GetString(box.Payload, handler + 8, 4);
                track.Kind = handlerType switch
                {
                    "vide" => TrackKind.Video,
                    "soun" => TrackKind.Audio,
                    _ => track.Kind
                };
            }

            var mdhd = FindBoxRecursive(box.Payload, "mdhd");
            if (mdhd != null)
            {
                var p = mdhd.Value.Payload;
                if (p.Length >= 16)
                    track.TimeScale = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12, 4));
                else if (p.Length >= 12)
                    track.TimeScale = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(8, 4));
            }

            if (track.Kind == TrackKind.Video)
            {
                var avc = FindBoxRecursive(box.Payload, "avcC");
                if (avc != null)
                {
                    track.Codec = VideoCodec.H264;
                    track.NalLengthSize = (avc.Value.Payload[4] & 3) + 1;
                    track.Extradata = avc.Value.Payload;
                }
                else
                {
                    var hvc = FindBoxRecursive(box.Payload, "hvcC");
                    if (hvc != null)
                    {
                        track.Codec = VideoCodec.H265;
                        track.NalLengthSize = (hvc.Value.Payload[21] & 3) + 1;
                        track.Extradata = hvc.Value.Payload;
                    }
                }
            }
            else if (track.Kind == TrackKind.Audio)
            {
                var esds = FindBoxRecursive(box.Payload, "esds");
                if (esds != null)
                {
                    track.AudioSpecificConfig = FindDescriptor(esds.Value.Payload, 0x05);
                    if (track.AudioSpecificConfig is { Length: >= 2 })
                        ParseAacConfig(track);
                }
            }
        }

        if (track.TimeScale == 0)
            track.TimeScale = track.Kind == TrackKind.Audio ? track.SampleRate : 90000;
    }

    private void ParseFragment(byte[] moof, byte[] mdat, Track track, List<Fragment> target)
    {
        foreach (var traf in FindBoxesRecursive(moof, "traf"))
        {
            var tfhd = FindBoxRecursive(traf.Payload, "tfhd");
            var tfdt = FindBoxRecursive(traf.Payload, "tfdt");
            var trun = FindBoxRecursive(traf.Payload, "trun");
            if (trun == null) continue;

            ulong decodeTime = 0;
            if (tfdt != null)
            {
                var p = tfdt.Value.Payload;
                var version = p[0];
                decodeTime = version == 1 && p.Length >= 12
                    ? BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(4, 8))
                    : BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4, 4));
            }

            var p = trun.Value.Payload;
            if (p.Length < 8) continue;
            var flags = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(0, 4)) & 0xFFFFFF;
            var count = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4, 4));
            var pos = 8;
            if ((flags & 0x000001) != 0) pos += 4;
            if ((flags & 0x000004) != 0) pos += 4;

            uint defaultDuration = 0;
            uint defaultSize = 0;
            if (tfhd != null)
            {
                var h = tfhd.Value.Payload;
                if (h.Length >= 8)
                {
                    var hf = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(0, 4)) & 0xFFFFFF;
                    var hp = 8;
                    if ((hf & 0x000001) != 0) hp += 8;
                    if ((hf & 0x000002) != 0) hp += 4;
                    if ((hf & 0x000008) != 0 && hp + 4 <= h.Length) defaultDuration = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(hp, 4));
                    if ((hf & 0x000010) != 0 && hp + 8 <= h.Length) defaultSize = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(hp + ((hf & 0x000008) != 0 ? 4 : 0), 4));
                }
            }

            var samples = new List<Sample>();
            var dataPos = 0;
            for (var i = 0; i < count; i++)
            {
                uint duration = defaultDuration;
                uint size = defaultSize;
                int sampleFlags = 0;
                int cto = 0;
                if ((flags & 0x000100) != 0) { duration = ReadU32(p, ref pos); }
                if ((flags & 0x000200) != 0) { size = ReadU32(p, ref pos); }
                if ((flags & 0x000400) != 0) { sampleFlags = (int)ReadU32(p, ref pos); }
                if ((flags & 0x000800) != 0)
                {
                    cto = (int)ReadU32(p, ref pos);
                }
                if (size == 0 || dataPos + size > mdat.Length) break;
                samples.Add(new Sample
                {
                    Data = mdat.AsSpan(dataPos, checked((int)size)).ToArray(),
                    Dts = decodeTime,
                    Duration = duration,
                    CompositionOffset = cto,
                    IsSync = (sampleFlags & 0x10000) == 0
                });
                decodeTime += duration;
                dataPos += checked((int)size);
            }

            if (samples.Count > 0)
                target.Add(new Fragment(samples));
        }
    }

    private async Task EmitFragmentAsync(Track track, Fragment fragment)
    {
        // If the companion track is not known yet, hold this fragment briefly by using
        // the async scheduler. The pipe reader for the other track runs concurrently.
        while (_video == null || _audio == null)
            await Task.Delay(10);

        if (!_tablesWritten)
        {
            WritePsi();
            _tablesWritten = true;
        }

        foreach (var sample in fragment.Samples)
        {
            var pts = Scale90k((long)sample.Dts + sample.CompositionOffset, track.TimeScale);
            var dts = Scale90k((long)sample.Dts, track.TimeScale);
            if (track.Kind == TrackKind.Video)
            {
                var payload = ConvertVideoSample(sample.Data, track);
                WritePes(payload, VideoPid, pts, dts, true, track.Codec == VideoCodec.H265 ? 0x24 : 0x1B);
            }
            else
            {
                var payload = AddAdts(sample.Data, track);
                WritePes(payload, AudioPid, pts, pts, false, 0x0F);
            }
        }
        await Task.CompletedTask;
    }

    private void WritePsi()
    {
        var pat = new byte[188];
        Array.Fill(pat, (byte)0xFF);
        pat[0] = 0x47; pat[1] = 0x40; pat[2] = 0x00; pat[3] = 0x10; pat[4] = 0;
        var patPayload = new byte[16];
        patPayload[0] = 0x00; patPayload[1] = 0xB0; patPayload[2] = 0x0D;
        patPayload[3] = 0x00; patPayload[4] = 0x01; patPayload[5] = 0xC1; patPayload[6] = 0x00; patPayload[7] = 0x00;
        patPayload[8] = 0x00; patPayload[9] = 0x01; patPayload[10] = 0xF0; patPayload[11] = 0x00;
        WriteCrc(patPayload, 8, 4);
        Buffer.BlockCopy(patPayload, 0, pat, 5, 12);
        WriteTs(pat);

        var pmt = new byte[188];
        Array.Fill(pmt, (byte)0xFF);
        pmt[0] = 0x47; pmt[1] = 0x50; pmt[2] = 0x00; pmt[3] = 0x10; pmt[4] = 0;
        var body = new byte[24];
        body[0] = 0x02; body[1] = 0xB0; body[2] = 0x17; body[3] = 0; body[4] = 1; body[5] = 0xC1; body[6] = 0; body[7] = 0;
        body[8] = 0xE1; body[9] = 0; body[10] = 0xF0; body[11] = 0;
        body[12] = (byte)(_video?.Codec == VideoCodec.H265 ? 0x24 : 0x1B); body[13] = 0xF1; body[14] = 0; body[15] = 0xF0; body[16] = 0;
        body[17] = 0x0F; body[18] = 0xF1; body[19] = 1; body[20] = 0xF0; body[21] = 0;
        WriteCrc(body, 0, 22);
        Buffer.BlockCopy(body, 0, pmt, 5, 22);
        WriteTs(pmt);
    }

    private void WritePes(byte[] payload, int pid, long pts, long dts, bool randomAccess, int streamType)
    {
        var pesHeader = new byte[19];
        pesHeader[0] = 0; pesHeader[1] = 0; pesHeader[2] = 1;
        pesHeader[3] = (byte)(pid == VideoPid ? 0xE0 : 0xC0);
        var headerLen = dts != pts ? 10 : 5;
        pesHeader[6] = (byte)(dts != pts ? 0xC0 : 0x80);
        pesHeader[7] = (byte)headerLen;
        WritePts(pesHeader.AsSpan(8), dts != pts ? 0x30 : 0x20, pts);
        if (dts != pts) WritePts(pesHeader.AsSpan(13), 0x10, dts);
        var pesLen = payload.Length + 3 + headerLen;
        BinaryPrimitives.WriteUInt16BigEndian(pesHeader.AsSpan(4, 2), (ushort)Math.Min(pesLen, 0xFFFF));

        var first = true;
        var offset = 0;
        while (offset < payload.Length || first)
        {
            var packet = new byte[188];
            Array.Fill(packet, (byte)0xFF);
            packet[0] = 0x47;
            packet[1] = (byte)(((pid >> 8) & 0x1F) | (first ? 0x40 : 0));
            packet[2] = (byte)pid;
            packet[3] = (byte)(0x10 | ((pid == VideoPid && randomAccess && first) ? 0x20 : 0));
            var pos = 4;
            if (pid == VideoPid && randomAccess && first)
            {
                packet[3] = 0x30;
                packet[4] = 1;
                packet[5] = 0x40;
                pos = 6;
            }
            var available = 188 - pos;
            var pesRemaining = pesHeader.Length - (first ? 0 : 0);
            if (first)
            {
                var takeHeader = Math.Min(available, pesHeader.Length);
                Buffer.BlockCopy(pesHeader, 0, packet, pos, takeHeader);
                pos += takeHeader;
                available -= takeHeader;
                if (takeHeader < pesHeader.Length)
                    throw new InvalidDataException("Unexpected PES header split");
            }
            var take = Math.Min(available, payload.Length - offset);
            if (take > 0)
            {
                Buffer.BlockCopy(payload, offset, packet, pos, take);
                offset += take;
            }
            WriteTs(packet);
            first = false;
        }
    }

    private void WriteTs(byte[] packet)
    {
        lock (_writeLock) _output.Write(packet, 0, packet.Length);
    }

    private static byte[] ConvertVideoSample(byte[] data, Track track)
    {
        using var ms = new MemoryStream(data.Length + 64);
        var pos = 0;
        while (pos + track.NalLengthSize <= data.Length)
        {
            var len = track.NalLengthSize switch
            {
                1 => data[pos],
                2 => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2)),
                3 => (data[pos] << 16) | (data[pos + 1] << 8) | data[pos + 2],
                _ => checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4)))
            };
            pos += track.NalLengthSize;
            if (len <= 0 || pos + len > data.Length) break;
            ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(1);
            ms.Write(data, pos, len);
            pos += len;
        }
        return ms.ToArray();
    }

    private static byte[] AddAdts(byte[] aac, Track track)
    {
        var profile = Math.Clamp(track.AacProfile - 1, 0, 3);
        var freq = track.AacFrequencyIndex;
        var channels = track.AacChannels;
        var frameLength = aac.Length + 7;
        var h = new byte[7 + aac.Length];
        h[0] = 0xFF; h[1] = 0xF1;
        h[2] = (byte)((profile << 6) | (freq << 2) | (channels >> 2));
        h[3] = (byte)(((channels & 3) << 6) | ((frameLength >> 11) & 3));
        h[4] = (byte)(frameLength >> 3); h[5] = (byte)(((frameLength & 7) << 5) | 0x1F); h[6] = 0xFC;
        Buffer.BlockCopy(aac, 0, h, 7, aac.Length);
        return h;
    }

    private static void ParseAacConfig(Track t)
    {
        var a = t.AudioSpecificConfig!;
        var objectType = a[0] >> 3;
        var freqIndex = ((a[0] & 7) << 1) | (a[1] >> 7);
        var sampleRate = freqIndex == 15 ? ((a[1] & 0x7F) << 17) : Frequency(freqIndex);
        var channels = (a[1] >> 3) & 0x0F;
        t.AacProfile = objectType;
        t.AacFrequencyIndex = freqIndex;
        t.SampleRate = sampleRate;
        t.AacChannels = channels == 0 ? 2 : channels;
    }

    private static int Frequency(int index) => index switch
    {
        0 => 96000, 1 => 88200, 2 => 64000, 3 => 48000, 4 => 44100, 5 => 32000,
        6 => 24000, 7 => 22050, 8 => 16000, 9 => 12000, 10 => 11025, 11 => 8000, 12 => 7350, _ => 48000
    };

    private static long Scale90k(long value, uint scale) => scale == 0 ? value : value * 90000L / scale;

    private static uint ReadU32(byte[] p, ref int pos)
    {
        var v = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(pos, 4)); pos += 4; return v;
    }

    private static void WritePts(Span<byte> dst, int prefix, long value)
    {
        value &= 0x1FFFFFFFFL;
        dst[0] = (byte)(prefix | ((value >> 30) & 7) << 1 | 1);
        dst[1] = (byte)(value >> 22);
        dst[2] = (byte)(((value >> 15) & 0x7F) << 1 | 1);
        dst[3] = (byte)(value >> 7);
        dst[4] = (byte)(((value & 0x7F) << 1) | 1);
    }

    private static void WriteCrc(byte[] data, int start, int length)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = start; i < start + length; i++)
        {
            crc ^= (uint)data[i] << 24;
            for (var j = 0; j < 8; j++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(start + length, 4), crc);
    }

    private static int FindBytes(byte[] data, string text)
    {
        var b = Encoding.ASCII.GetBytes(text);
        for (var i = 0; i <= data.Length - b.Length; i++)
            if (data.AsSpan(i, b.Length).SequenceEqual(b)) return i;
        return -1;
    }

    private static byte[]? FindDescriptor(byte[] data, byte tag)
    {
        for (var i = 0; i < data.Length - 2; i++)
        {
            if (data[i] != tag) continue;
            var p = i + 1;
            var len = 0;
            for (var n = 0; n < 4 && p < data.Length; n++)
            {
                var c = data[p++]; len = (len << 7) | (c & 0x7F);
                if ((c & 0x80) == 0) return p + len <= data.Length ? data[p..(p + len)] : null;
            }
        }
        return null;
    }

    private static Box? FindBoxRecursive(byte[] data, string type)
        => FindBoxesRecursive(data, type).FirstOrDefault();

    private static IEnumerable<Box> FindBoxesRecursive(byte[] data, string type)
    {
        foreach (var b in Boxes(data))
        {
            if (b.Type == type) yield return b;
            foreach (var child in FindBoxesRecursive(b.Payload, type)) yield return child;
        }
    }

    private static IEnumerable<Box> Boxes(byte[] data)
    {
        var pos = 0;
        while (pos + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            var type = Encoding.ASCII.GetString(data, pos + 4, 4);
            var header = 8;
            long actual = size;
            if (size == 1 && pos + 16 <= data.Length) { actual = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8, 8)); header = 16; }
            if (actual < header || pos + actual > data.Length) yield break;
            yield return new Box(type, data.AsSpan(pos + header, checked((int)actual - header)).ToArray());
            pos += checked((int)actual);
        }
    }

    private readonly record struct Box(string Type, byte[] Payload);
    private readonly record struct Fragment(List<Sample> Samples);
    private sealed class Sample
    {
        public byte[] Data = [];
        public ulong Dts;
        public uint Duration;
        public int CompositionOffset;
        public bool IsSync;
    }

    private enum TrackKind { Unknown, Video, Audio }
    private enum VideoCodec { H264, H265 }

    private sealed class Track
    {
        public TrackKind Kind;
        public VideoCodec Codec;
        public uint TimeScale;
        public int NalLengthSize = 4;
        public byte[]? Extradata;
        public byte[]? AudioSpecificConfig;
        public int AacProfile = 2;
        public int AacFrequencyIndex = 4;
        public int SampleRate = 44100;
        public int AacChannels = 2;
    }

    private sealed class FragmentReader
    {
        private readonly Stream _stream;
        public FragmentReader(Stream stream) => _stream = stream;

        public async Task<Box?> ReadBoxAsync()
        {
            var header = new byte[8];
            var got = await ReadExactlyAsync(header);
            if (got == 0) return null;
            if (got != 8) throw new EndOfStreamException("Truncated fMP4 box header");
            var size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var headerSize = 8;
            long total = size;
            if (size == 1)
            {
                var ext = new byte[8];
                if (await ReadExactlyAsync(ext) != 8) throw new EndOfStreamException();
                total = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
                headerSize = 16;
            }
            if (total < headerSize || total > int.MaxValue) throw new InvalidDataException($"Invalid MP4 box size {total}");
            var payload = new byte[checked((int)total - headerSize)];
            if (await ReadExactlyAsync(payload) != payload.Length) throw new EndOfStreamException("Truncated fMP4 box");
            return new Box(type, payload);
        }

        private async Task<int> ReadExactlyAsync(byte[] buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var n = await _stream.ReadAsync(buffer.AsMemory(total));
                if (n == 0) break;
                total += n;
            }
            return total;
        }
    }
}
