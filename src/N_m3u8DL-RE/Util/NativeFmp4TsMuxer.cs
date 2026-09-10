using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using N_m3u8DL_RE.Common.Log;
using Spectre.Console;

namespace N_m3u8DL_RE.Util;

/// <summary>FFmpeg-free live fMP4 (H.264/H.265 + AAC) to MPEG-TS muxer.</summary>
internal sealed class NativeFmp4TsMuxer
{
    private const int VideoPid = 0x100, AudioPid = 0x101, PmtPid = 0x1000;
    private readonly Stream output;
    private readonly object gate = new();
    private int videoCc, audioCc, pmtCc;
    private Track? video, audio;
    private bool psiWritten;
    private bool emitWaitLogged;
    private long videoSamples, audioSamples, videoBytes, audioBytes, moofCount;
    private long tsPacketsWritten, tsBytesWritten, pesWrites;

    private NativeFmp4TsMuxer(Stream output) => this.output = output;

    public static async Task<bool> RunAsync(string[] pipeNames, string outputPath)
    {
        Logger.InfoMarkUp($"[yellow]Native mux entry: inputs={pipeNames.Length}; output={outputPath.EscapeMarkup()}[/]");
        if (pipeNames.Length != 2) { Logger.ErrorMarkUp("[red]Native mux requires exactly two non-subtitle streams (video + audio).[/]"); return false; }
        try
        {
            Logger.InfoMarkUp($"[yellow]Native mux opening input pipe 0: {pipeNames[0].EscapeMarkup()}[/]");
            await using var p0 = OpenPipe(pipeNames[0]);
            Logger.InfoMarkUp("[green]Native mux input pipe 0 opened.[/]");
            Logger.InfoMarkUp($"[yellow]Native mux opening input pipe 1: {pipeNames[1].EscapeMarkup()}[/]");
            await using var p1 = OpenPipe(pipeNames[1]);
            Logger.InfoMarkUp("[green]Native mux input pipe 1 opened.[/]");

            var anonymousPipe = IsAnonymousPipeOutput(outputPath);
            Logger.InfoMarkUp($"[yellow]Native mux opening MPEG-TS output: {outputPath.EscapeMarkup()} anonymousPipe={anonymousPipe}[/]");
            await using var dst = OpenOutput(outputPath);
            Logger.InfoMarkUp("[green]Native mux MPEG-TS output opened.[/]");
            var mux = new NativeFmp4TsMuxer(dst);
            await Task.WhenAll(mux.ReadPipeAsync(p0, 0), mux.ReadPipeAsync(p1, 1));
            await dst.FlushAsync();
            Logger.InfoMarkUp($"[deepskyblue1]Native mux complete: moof={mux.moofCount}; videoSamples={mux.videoSamples}; audioSamples={mux.audioSamples}; videoBytes={mux.videoBytes}; audioBytes={mux.audioBytes}; pesWrites={mux.pesWrites}; tsPackets={mux.tsPacketsWritten}; tsBytes={mux.tsBytesWritten}; psi={mux.psiWritten}[/]");
            return mux.psiWritten;
        }
        catch (Exception ex) { Logger.ErrorMarkUp($"[red]Native fMP4 mux failed: {ex.GetType().Name}: {ex.Message.EscapeMarkup()}[/]"); return false; }
    }

    private static bool IsAnonymousPipeOutput(string path) => path.StartsWith("/proc/self/fd/", StringComparison.Ordinal);

    private static Stream OpenOutput(string path)
    {
        if (IsAnonymousPipeOutput(path))
        {
            if (!int.TryParse(path[14..], out var fd) || fd < 0)
                throw new ArgumentException($"Invalid anonymous pipe fd path: {path}");
            var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: false);
            return new FileStream(handle, FileAccess.Write, 1024 * 1024, isAsync: false);
        }
        return new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static Stream OpenPipe(string name) => OperatingSystem.IsWindows()
        ? new FileStream($"\\\\.\\pipe\\{name}", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.Asynchronous)
        : new FileStream(Path.Combine(Path.GetTempPath(), name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.Asynchronous);

    private async Task ReadPipeAsync(Stream pipe, int pipeIndex)
    {
        Logger.InfoMarkUp($"[yellow]Native mux reader {pipeIndex} started.[/]");
        var r = new BoxReader(pipe); Track? track = null;
        while (true)
        {
            var box = await r.ReadAsync();
            if (box == null) { Logger.InfoMarkUp($"[yellow]Native mux reader {pipeIndex}: input EOF.[/]"); break; }
            if (box.Value.Type == "moov")
            {
                Logger.WarnMarkUp($"[PIPE-DIAG] reader={pipeIndex} received moov bytes={box.Value.Payload.Length}");
                LogBoxTree(box.Value.Payload, "moov", 0, 4);
                track = ParseInit(box.Value.Payload, pipeIndex);
                if (track.Kind == Kind.Video) { video = track; Logger.InfoMarkUp($"[green]Native mux reader {pipeIndex}: video init codec={track.Codec}; timescale={track.TimeScale}; nalLength={track.NalLengthSize}.[/]"); }
                else { audio = track; Logger.InfoMarkUp($"[green]Native mux reader {pipeIndex}: audio init timescale={track.TimeScale}; AAC profile={track.AacProfile}; freqIndex={track.AacFreq}; channels={track.Channels}.[/]"); }
                continue;
            }
            if (box.Value.Type != "moof") { Logger.WarnMarkUp($"[PIPE-DIAG] reader={pipeIndex} ignoring box={box.Value.Type} bytes={box.Value.Payload.Length}"); continue; }
            moofCount++;
            var mdat = await r.ReadAsync();
            if (mdat == null || mdat.Value.Type != "mdat") throw new InvalidDataException("moof is not followed by mdat");
            if (track == null) { Logger.WarnMarkUp($"[PIPE-DIAG] reader={pipeIndex} received media before init: moof bytes={box.Value.Payload.Length}, mdat bytes={mdat.Value.Payload.Length}"); continue; }
            var samples = ParseFragment(box.Value.Payload, mdat.Value.Payload, track);
            Logger.InfoMarkUp($"[yellow]Native mux reader {pipeIndex}: moof #{moofCount} track={track.Kind} mdat={mdat.Value.Payload.Length} samples={samples.Count}.[/]");
            foreach (var sample in samples) await EmitAsync(track, sample);
        }
    }

    private static void LogBoxTree(byte[] payload, string rootName, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        foreach (var box in Boxes(payload))
        {
            Logger.WarnMarkUp($"[PIPE-DIAG] {new string(' ', depth * 2)}{box.Type} bytes={box.Payload.Length}");
            if (box.Type is "trak" or "mdia" or "minf" or "stbl" or "stsd" or "avc1" or "avc3" or "hvc1" or "hev1" or "encv" or "mp4a" or "enca" or "moov" or "edts" or "dinf" or "mvex" or "moof" or "traf") LogBoxTree(box.Payload, box.Type, depth + 1, maxDepth);
        }
    }

    private async Task EmitAsync(Track t, Sample s)
    {
        if (video == null || audio == null)
        {
            if (!emitWaitLogged)
            {
                emitWaitLogged = true;
                Logger.WarnMarkUp($"[PIPE-TS] Emit waiting for both tracks: current={(t.Kind == Kind.Video ? "video" : "audio")}; videoInit={(video != null)}; audioInit={(audio != null)}; sampleBytes={s.Data.Length}");
            }
            while (video == null || audio == null) await Task.Delay(5);
            Logger.WarnMarkUp("[PIPE-TS] Emit wait released: both video and audio init tracks are available.");
        }

        lock (gate)
        {
            if (!psiWritten)
            {
                Logger.WarnMarkUp($"[PIPE-TS] Writing PAT/PMT before first PES: videoCodec={video!.Codec}; videoPid=0x{VideoPid:X}; audioPid=0x{AudioPid:X}; pmtPid=0x{PmtPid:X}");
                WritePsi(); psiWritten = true; Logger.InfoMarkUp("[green]Native mux emitted PAT/PMT.[/]");
            }
            var pts = Scale90((long)s.Dts + s.Cto, t.TimeScale); var dts = Scale90((long)s.Dts, t.TimeScale);
            if (t.Kind == Kind.Video) { var payload = ConvertVideo(s.Data, t); videoSamples++; videoBytes += payload.Length; WritePes(payload, VideoPid, pts, dts, true, ref videoCc); }
            else { var payload = AddAdts(s.Data, t); audioSamples++; audioBytes += payload.Length; WritePes(payload, AudioPid, pts, pts, false, ref audioCc); }
        }
        await Task.CompletedTask;
    }

    private Track ParseInit(byte[] moov, int pipeIndex)
    {
        foreach (var trak in FindBoxes(moov, "trak"))
        {
            var h = FindBox(trak.Payload, "hdlr"); if (h == null || h.Value.Payload.Length < 12) continue;
            var hp = h.Value.Payload; var handler = Encoding.ASCII.GetString(hp, 8, 4); var t = new Track { Kind = handler == "vide" ? Kind.Video : handler == "soun" ? Kind.Audio : Kind.Unknown }; if (t.Kind == Kind.Unknown) continue;
            Logger.WarnMarkUp($"[PIPE-DIAG] reader={pipeIndex} track handler={handler}");
            var tkhd = FindBox(trak.Payload, "tkhd"); if (tkhd != null) { var p = tkhd.Value.Payload; if (p.Length >= 20 && p[0] == 0) t.TrackId = ReadU32(p, 12); else if (p.Length >= 32 && p[0] == 1) t.TrackId = ReadU32(p, 20); }
            var mdhd = FindBox(trak.Payload, "mdhd"); if (mdhd != null) { var p = mdhd.Value.Payload; if (p.Length >= 24 && p[0] == 1) t.TimeScale = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(20, 4)); else if (p.Length >= 16) t.TimeScale = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12, 4)); }
            if (t.TimeScale == 0) t.TimeScale = t.Kind == Kind.Audio ? 48000u : 90000u;
            var stsd = FindBox(trak.Payload, "stsd"); if (stsd == null) throw new InvalidDataException("Track has no stsd"); var entry = FindSampleEntry(stsd.Value.Payload, t.Kind == Kind.Video); if (entry == null) throw new InvalidDataException($"No supported {(t.Kind == Kind.Video ? "video" : "audio")} sample entry");
            Logger.WarnMarkUp($"[PIPE-DIAG] reader={pipeIndex} sample-entry={entry.Value.Type} bytes={entry.Value.Payload.Length}");
            if (t.Kind == Kind.Video)
            {
                var avc = FindBoxInSampleEntry(entry.Value.Payload, entry.Value.Type, "avcC"); var hvc = FindBoxInSampleEntry(entry.Value.Payload, entry.Value.Type, "hvcC"); Logger.WarnMarkUp($"[PIPE-DIAG] reader={pipeIndex} video avcC={(avc != null ? avc.Value.Payload.Length : 0)} hvcC={(hvc != null ? hvc.Value.Payload.Length : 0)}");
                if (avc != null && avc.Value.Payload.Length >= 5) { t.Codec = Codec.H264; t.NalLengthSize = (avc.Value.Payload[4] & 3) + 1; } else if (hvc != null && hvc.Value.Payload.Length >= 22) { t.Codec = Codec.H265; t.NalLengthSize = (hvc.Value.Payload[21] & 3) + 1; } else throw new InvalidDataException("Video init has neither avcC nor hvcC");
            }
            else
            {
                var esds = FindBoxInSampleEntry(entry.Value.Payload, entry.Value.Type, "esds"); var asc = esds == null ? null : FindDescriptor(esds.Value.Payload, 0x05); Logger.WarnMarkUp($"[PIPE-DIAG] reader={pipeIndex} audio esds={(esds != null ? esds.Value.Payload.Length : 0)} asc={(asc != null ? asc.Length : 0)}"); if (asc == null || asc.Length < 2) throw new InvalidDataException("AAC init has no AudioSpecificConfig");
                t.Asc = asc; var a0 = asc[0]; var a1 = asc[1]; t.AacProfile = Math.Max(1, a0 >> 3); t.AacFreq = ((a0 & 7) << 1) | (a1 >> 7); t.SampleRate = Frequency(t.AacFreq); t.Channels = (a1 >> 3) & 15; if (t.Channels == 0) t.Channels = 2;
            }
            foreach (var trex in FindBoxes(moov, "trex")) { var p = trex.Payload; if (p.Length < 24) continue; var trackId = ReadU32(p, 4); if (trackId != t.TrackId) continue; t.DefaultSampleDescriptionIndex = ReadU32(p, 8); t.TrexDefaultDuration = ReadU32(p, 12); t.TrexDefaultSize = ReadU32(p, 16); t.TrexDefaultFlags = ReadU32(p, 20); Logger.WarnMarkUp($"[PIPE-DIAG] reader={pipeIndex} trex track={trackId} defDur={t.TrexDefaultDuration} defSize={t.TrexDefaultSize} defFlags=0x{t.TrexDefaultFlags:X8}"); break; }
            return t;
        }
        throw new InvalidDataException("No supported track in moov");
    }

    private static Box? FindSampleEntry(byte[] stsd, bool video) { if (stsd.Length < 8) return null; var count = BinaryPrimitives.ReadUInt32BigEndian(stsd.AsSpan(4, 4)); var pos = 8; for (uint i = 0; i < count && pos + 8 <= stsd.Length; i++) { var size = BinaryPrimitives.ReadUInt32BigEndian(stsd.AsSpan(pos, 4)); if (size < 8 || size > stsd.Length - pos) return null; var type = Encoding.ASCII.GetString(stsd, pos + 4, 4); var payload = stsd.AsSpan(pos + 8, checked((int)size - 8)).ToArray(); if ((video && IsVideoSampleEntry(type)) || (!video && IsAudioSampleEntry(type))) return new Box(type, payload); pos += checked((int)size); } return null; }
    private static Box? FindBoxInSampleEntry(byte[] payload, string entryType, string target) { var fixedHeader = IsVideoSampleEntry(entryType) ? 78 : IsAudioSampleEntry(entryType) ? 28 : 0; if (payload.Length < fixedHeader) return null; var children = payload.AsSpan(fixedHeader).ToArray(); var found = FindBox(children, target); if (found != null) return found; foreach (var child in Boxes(children)) if (IsVideoSampleEntry(child.Type) || IsAudioSampleEntry(child.Type)) { found = FindBoxInSampleEntry(child.Payload, child.Type, target); if (found != null) return found; } return null; }
    private static bool IsVideoSampleEntry(string type) => type is "avc1" or "avc3" or "hvc1" or "hev1" or "encv";
    private static bool IsAudioSampleEntry(string type) => type is "mp4a" or "enca";

    private static List<Sample> ParseFragment(byte[] moof, byte[] mdat, Track t)
    {
        var result = new List<Sample>(); foreach (var traf in FindBoxes(moof, "traf"))
        {
            var tfhd = FindBox(traf.Payload, "tfhd"); var tfdt = FindBox(traf.Payload, "tfdt"); var trun = FindBox(traf.Payload, "trun"); if (trun == null) continue; var tfhdPayload = tfhd?.Payload; var trunPayload = trun.Value.Payload; if (trunPayload.Length < 8) { Logger.WarnMarkUp($"[PIPE-DIAG] fragment {t.Kind}: malformed trun"); continue; }
            var flags = BinaryPrimitives.ReadUInt32BigEndian(trunPayload.AsSpan(0, 4)); var count = BinaryPrimitives.ReadUInt32BigEndian(trunPayload.AsSpan(4, 4)); var pos = 8;
            long dts = tfdt == null ? 0 : ParseTfdt(tfdt.Value.Payload); var dataOffset = 0; if ((flags & 0x000001) != 0) { if (pos + 4 > trunPayload.Length) throw new InvalidDataException("trun data_offset missing"); dataOffset = BinaryPrimitives.ReadInt32BigEndian(trunPayload.AsSpan(pos, 4)); pos += 4; }
            uint firstFlags = 0; if ((flags & 0x000004) != 0) { if (pos + 4 > trunPayload.Length) throw new InvalidDataException("trun first_sample_flags missing"); firstFlags = BinaryPrimitives.ReadUInt32BigEndian(trunPayload.AsSpan(pos, 4)); pos += 4; }
            var defaultDuration = ReadDefaultDuration(tfhdPayload, t); var defaultSize = ReadDefaultSize(tfhdPayload, t); var defaultFlags = ReadDefaultFlags(tfhdPayload, t);
            var sampleBase = dataOffset != 0 ? dataOffset : 0;
            for (uint i = 0; i < count; i++)
            {
                uint dur = defaultDuration, size = defaultSize, sflags = i == 0 && (flags & 0x000004) != 0 ? firstFlags : defaultFlags; int cto = 0;
                if ((flags & 0x000100) != 0) { if (pos + 4 > trunPayload.Length) throw new InvalidDataException("trun sample duration missing"); dur = BinaryPrimitives.ReadUInt32BigEndian(trunPayload.AsSpan(pos, 4)); pos += 4; }
                if ((flags & 0x000200) != 0) { if (pos + 4 > trunPayload.Length) throw new InvalidDataException("trun sample size missing"); size = BinaryPrimitives.ReadUInt32BigEndian(trunPayload.AsSpan(pos, 4)); pos += 4; }
                if ((flags & 0x000400) != 0) { if (pos + 4 > trunPayload.Length) throw new InvalidDataException("trun sample flags missing"); sflags = BinaryPrimitives.ReadUInt32BigEndian(trunPayload.AsSpan(pos, 4)); pos += 4; }
                if ((flags & 0x000800) != 0) { if (pos + 4 > trunPayload.Length) throw new InvalidDataException("trun sample cto missing"); cto = BinaryPrimitives.ReadInt32BigEndian(trunPayload.AsSpan(pos, 4)); pos += 4; }
                var off = sampleBase; if (off < 0 || off > mdat.Length || size > mdat.Length - off) { Logger.WarnMarkUp($"[PIPE-DIAG] sample {i} exceeds mdat: offset={off} size={size} mdat={mdat.Length}; clamping"); size = (uint)Math.Max(0, mdat.Length - Math.Max(0, off)); }
                result.Add(new Sample(mdat.AsSpan(off, checked((int)size)).ToArray(), dts, cto, dur, sflags)); sampleBase = checked(sampleBase + (int)size); dts += dur;
            }
        }
        return result;
    }

    private static long ParseTfdt(byte[] p) { if (p.Length < 8) return 0; return p[0] == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(4, 8)) : BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4, 4)); }
    private static uint ReadU32(byte[] p, int offset) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(offset, 4));
    private static uint ReadDefaultDuration(byte[]? p, Track t) => p != null && p.Length >= 16 && (BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(0, 4)) & 8) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12, 4)) : t.TrexDefaultDuration;
    private static uint ReadDefaultSize(byte[]? p, Track t) => p != null && p.Length >= 20 && (BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(0, 4)) & 16) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(16, 4)) : t.TrexDefaultSize;
    private static uint ReadDefaultFlags(byte[]? p, Track t) => p != null && p.Length >= 24 && (BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(0, 4)) & 32) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(20, 4)) : t.TrexDefaultFlags;

    private static byte[] ConvertVideo(byte[] sample, Track t)
    {
        var result = new List<byte>(sample.Length + 64); var pos = 0; while (pos + t.NalLengthSize <= sample.Length) { uint n = t.NalLengthSize switch { 1 => sample[pos], 2 => BinaryPrimitives.ReadUInt16BigEndian(sample.AsSpan(pos, 2)), 4 => BinaryPrimitives.ReadUInt32BigEndian(sample.AsSpan(pos, 4)), _ => throw new InvalidDataException("Unsupported NAL length size") }; pos += t.NalLengthSize; if (n > sample.Length - pos) n = (uint)(sample.Length - pos); result.Add(0); result.Add(0); result.Add(0); result.Add(1); result.AddRange(sample.AsSpan(pos, checked((int)n)).ToArray()); pos += checked((int)n); } return result.ToArray();
    }

    private static byte[] AddAdts(byte[] aac, Track t)
    {
        var profile = Math.Max(1, t.AacProfile) - 1; var len = aac.Length + 7; var h = new byte[7]; h[0] = 0xFF; h[1] = 0xF1; h[2] = (byte)((profile << 6) | ((t.AacFreq & 15) << 2) | ((t.Channels >> 2) & 1)); h[3] = (byte)(((t.Channels & 3) << 6) | ((len >> 11) & 3)); h[4] = (byte)((len >> 3) & 0xFF); h[5] = (byte)(((len & 7) << 5) | 0x1F); h[6] = 0xFC; var result = new byte[len]; Buffer.BlockCopy(h, 0, result, 0, 7); Buffer.BlockCopy(aac, 0, result, 7, aac.Length); return result;
    }

    private static long Scale90(long value, uint scale) => scale == 0 ? 0 : (long)Math.Round(value * 90000.0 / scale);

    private void WritePsi()
    {
        var pat = new byte[188]; var pmt = new byte[188]; FillTs(pat, 0, true, ref pmtCc); var p = 4; pat[p++] = 0; pat[p++] = 0xB0; pat[p++] = 0x0D; pat[p++] = 0; pat[p++] = 1; pat[p++] = 0xC1; pat[p++] = 0; pat[p++] = 0; pat[p++] = 0; pat[p++] = 1; pat[p++] = 0xE0; pat[p++] = (byte)(PmtPid & 0xFF); pat[p++] = 0; WriteCrc(pat, 5, p - 5); FillTs(pmt, PmtPid, true, ref pmtCc); p = 4; pmt[p++] = 0; pmt[p++] = 0xB0; pmt[p++] = 0x17; pmt[p++] = 0; pmt[p++] = 1; pmt[p++] = 0xC1; pmt[p++] = 0; pmt[p++] = 0x00; pmt[p++] = 0xE1; pmt[p++] = 0; pmt[p++] = 0xF0; pmt[p++] = 0; pmt[p++] = video?.Codec == Codec.H265 ? (byte)0x24 : (byte)0x1B; pmt[p++] = 0xE1; pmt[p++] = 0; pmt[p++] = 0xF0; pmt[p++] = 0; pmt[p++] = 0x0F; pmt[p++] = 0xE1; pmt[p++] = 1; pmt[p++] = 0xF0; pmt[p++] = 0; WriteCrc(pmt, 5, p - 5);
        WriteRaw(pat); WriteRaw(pmt);
    }

    private void WritePes(byte[] payload, int pid, long pts, long dts, bool isVideo, ref int cc)
    {
        pesWrites++; var ptsDtsFlags = isVideo && pts != dts ? 3 : 2; var headerLen = ptsDtsFlags == 3 ? 10 : 5; var pesLen = isVideo ? 0 : payload.Length + 3 + headerLen; var pes = new byte[14 + headerLen + payload.Length]; var q = 0; pes[q++] = 0; pes[q++] = 0; pes[q++] = 1; pes[q++] = isVideo ? (byte)0xE0 : (byte)0xC0; pes[q++] = (byte)(pesLen >> 8); pes[q++] = (byte)pesLen; pes[q++] = 0x80; pes[q++] = (byte)(ptsDtsFlags << 6); pes[q++] = (byte)headerLen; PutPts(pes, q, pts, ptsDtsFlags == 3 ? 3 : 2); q += 5; if (ptsDtsFlags == 3) { PutPts(pes, q, dts, 1); q += 5; } Buffer.BlockCopy(payload, 0, pes, q, payload.Length); WriteTsPackets(pes, pid, isVideo, ref cc, pts); }

    private void WriteTsPackets(byte[] pes, int pid, bool payloadUnitStart, ref int cc, long pts)
    {
        var pos = 0; var first = true; while (pos < pes.Length) { var ts = new byte[188]; FillTs(ts, pid, first, ref cc); var h = 4; var remain = pes.Length - pos; var cap = 184; if (remain < cap) { ts[3] = (byte)((ts[3] & 0xCF) | 0x30); var stuffing = cap - remain; ts[4] = (byte)(stuffing - 1); if (stuffing > 1) { ts[5] = 0; Array.Fill(ts, (byte)0xFF, 6, stuffing - 1); } h = 5 + stuffing - 1; } Array.Copy(pes, pos, ts, h, Math.Min(remain, 188 - h)); WriteRaw(ts); pos += Math.Min(remain, 188 - h); first = false; }
    }

    private void FillTs(byte[] ts, int pid, bool pusi, ref int cc)
    {
        Array.Fill(ts, (byte)0xFF); ts[0] = 0x47; ts[1] = (byte)((pusi ? 0x40 : 0) | ((pid >> 8) & 0x1F)); ts[2] = (byte)pid; ts[3] = (byte)(0x10 | (cc++ & 0x0F));
    }

    private void WriteRaw(byte[] ts) { output.Write(ts, 0, ts.Length); tsPacketsWritten++; tsBytesWritten += ts.Length; if ((tsPacketsWritten % 100) == 0) Logger.WarnMarkUp($"[PIPE-TS] raw TS packet #{tsPacketsWritten} bytes={ts.Length} cumulativeTsBytes={tsBytesWritten}"); }
    private static void WriteCrc(byte[] b, int off, int len) { uint crc = 0xFFFFFFFF; for (var i = off; i < len; i++) { crc ^= (uint)b[i] << 24; for (var j = 0; j < 8; j++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1; } b[len] = (byte)(crc >> 24); b[len + 1] = (byte)(crc >> 16); b[len + 2] = (byte)(crc >> 8); b[len + 3] = (byte)crc; }
    private static void PutPts(byte[] b, int off, long pts, int prefix) { ulong v = (ulong)Math.Max(0, pts) & ((1UL << 33) - 1); b[off] = (byte)(((ulong)(prefix << 4)) | (((v >> 30) & 7) << 1) | 1); b[off + 1] = (byte)(v >> 22); b[off + 2] = (byte)((((v >> 15) & 0x7F) << 1) | 1); b[off + 3] = (byte)(v >> 7); b[off + 4] = (byte)(((v & 0x7F) << 1) | 1); }

    private sealed class BoxReader
    {
        private readonly Stream s; public BoxReader(Stream s) => this.s = s;
        public async Task<Box?> ReadAsync() { var h = new byte[8]; var n = await ReadExact(h); if (n == 0) return null; if (n < 8) throw new EndOfStreamException(); var size = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(0, 4)); var type = Encoding.ASCII.GetString(h, 4, 4); long total = size == 1 ? await ReadUInt64() : size; if (total < 8 || total > int.MaxValue) throw new InvalidDataException($"Invalid box size {total} for {type}"); var payload = new byte[checked((int)total - 8)]; await ReadExact(payload); return new Box(type, payload); }
        private async Task<long> ReadUInt64() { var b = new byte[8]; await ReadExact(b); return checked((long)BinaryPrimitives.ReadUInt64BigEndian(b)); }
        private async Task<int> ReadExact(byte[] b) { var off = 0; while (off < b.Length) { var n = await s.ReadAsync(b.AsMemory(off)); if (n == 0) break; off += n; } return off; }
    }

    private readonly record struct Box(string Type, byte[] Payload);
    private enum Kind { Unknown, Video, Audio }
    private enum Codec { H264, H265 }
    private sealed class Track { public Kind Kind; public uint TrackId, TimeScale, TrexDefaultDuration, TrexDefaultSize, TrexDefaultFlags, DefaultSampleDescriptionIndex; public Codec Codec; public int NalLengthSize = 4, AacProfile = 2, AacFreq = 4, Channels = 2; public uint SampleRate = 48000; public byte[] Asc = Array.Empty<byte>(); }
    private readonly record struct Sample(byte[] Data, long Dts, int Cto, uint Duration, uint Flags);

    private static IEnumerable<Box> Boxes(byte[] data) { var pos = 0; while (pos + 8 <= data.Length) { var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4)); var type = Encoding.ASCII.GetString(data, pos + 4, 4); if (size < 8 || size > data.Length - pos) yield break; yield return new Box(type, data.AsSpan(pos + 8, checked((int)size - 8)).ToArray()); pos += checked((int)size); } }
    private static IEnumerable<Box> FindBoxes(byte[] data, string type) => Boxes(data).Where(b => b.Type == type);
    private static Box? FindBox(byte[] data, string type) => Boxes(data).FirstOrDefault(b => b.Type == type) is var b && b.Payload != null && b.Type == type ? b : null;
    private static byte[]? FindDescriptor(byte[] data, byte wanted) { for (var i = 4; i + 2 < data.Length; i++) if (data[i] == wanted) { var len = data[i + 1] & 0x7F; if (i + 2 + len <= data.Length) return data.AsSpan(i + 2, len).ToArray(); } return null; }
    private static uint Frequency(int idx) => idx switch { 0 => 96000, 1 => 88200, 2 => 64000, 3 => 48000, 4 => 44100, 5 => 32000, 6 => 24000, 7 => 22050, 8 => 16000, 9 => 12000, 10 => 11025, 11 => 8000, _ => 48000 };
}
