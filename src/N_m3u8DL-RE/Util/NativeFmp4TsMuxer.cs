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

            var leaveOpen = IsAnonymousPipeOutput(outputPath);
            Logger.InfoMarkUp($"[yellow]Native mux opening MPEG-TS output: {outputPath.EscapeMarkup()} anonymousPipe={leaveOpen}[/]");
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
            return new FileStream(handle, FileAccess.Write, 1024 * 1024, isAsync: true);
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
            var tfhd = FindBox(traf.Payload, "tfhd"); var tfdt = FindBox(traf.Payload, "tfdt"); var trun = FindBox(traf.Payload, "trun"); if (trun == null) continue; var tfhdPayload = tfhd?.Payload; var trunPayload = trun.Value.Payload; if (trunPayload.Length < 8) { Logger.WarnMarkUp($"[PIPE-DIAG] fragment {t.Kind}: malformed trun payload={trunPayload.Length}"); continue; }
            var tfhdFlags = tfhdPayload != null && tfhdPayload.Length >= 8 ? ReadU24(tfhdPayload, 0) : 0; var trunFlags = ReadU24(trunPayload, 0); var sampleCount = ReadU32(trunPayload, 4); Logger.WarnMarkUp($"[PIPE-DIAG] fragment {t.Kind}: tfhdFlags=0x{tfhdFlags:X6} trunFlags=0x{trunFlags:X6} samples={sampleCount} tfhdBytes={(tfhdPayload?.Length ?? 0)} trunBytes={trunPayload.Length} mdat={mdat.Length}");
            ulong dts = 0; if (tfdt != null && tfdt.Value.Payload.Length >= 8) { var p = tfdt.Value.Payload; if (p[0] == 1 && p.Length >= 12) dts = BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(4, 8)); else dts = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4, 4)); }
            uint defDur = 0, defSize = 0, defFlags = 0; long baseDataOffset = -1; if (tfhdPayload != null && tfhdPayload.Length >= 8) { var pos = 8; if ((tfhdFlags & 0x000001) != 0) { if (pos + 8 > tfhdPayload.Length) continue; baseDataOffset = unchecked((long)BinaryPrimitives.ReadUInt64BigEndian(tfhdPayload.AsSpan(pos, 8))); pos += 8; } if ((tfhdFlags & 0x000002) != 0) { if (pos + 4 > tfhdPayload.Length) continue; pos += 4; } if ((tfhdFlags & 0x000008) != 0) { if (pos + 4 > tfhdPayload.Length) continue; defDur = ReadU32(tfhdPayload, pos); pos += 4; } if ((tfhdFlags & 0x000010) != 0) { if (pos + 4 > tfhdPayload.Length) continue; defSize = ReadU32(tfhdPayload, pos); pos += 4; } if ((tfhdFlags & 0x000020) != 0) { if (pos + 4 > tfhdPayload.Length) continue; defFlags = ReadU32(tfhdPayload, pos); } }
            if (defDur == 0) defDur = t.TrexDefaultDuration; if (defSize == 0) defSize = t.TrexDefaultSize; if (defFlags == 0) defFlags = t.TrexDefaultFlags;
            var pos2 = 8; long dataPos; if ((trunFlags & 0x000001) != 0) { if (pos2 + 4 > trunPayload.Length) continue; var dataOffset = unchecked((int)ReadU32(trunPayload, pos2)); pos2 += 4; var moofBoxSize = 8L + moof.Length; dataPos = (tfhdFlags & 0x020000) != 0 ? dataOffset - moofBoxSize : baseDataOffset >= 0 ? baseDataOffset + dataOffset - moofBoxSize : dataOffset - moofBoxSize; Logger.WarnMarkUp($"[PIPE-DIAG] fragment {t.Kind}: dataOffset={dataOffset} moofBoxSize={moofBoxSize} resolvedDataPos={dataPos}"); } else dataPos = (tfhdFlags & 0x020000) != 0 || baseDataOffset < 0 ? 0 : baseDataOffset - (8L + moof.Length);
            if (dataPos < 0 || dataPos > mdat.Length) { Logger.WarnMarkUp($"[PIPE-DIAG] fragment {t.Kind}: dataPos out of range: {dataPos}, forcing 0"); dataPos = 0; }
            if ((trunFlags & 0x000004) != 0) { if (pos2 + 4 > trunPayload.Length) continue; pos2 += 4; }
            var entryBytes = trunPayload.Length - pos2; var compactDurationSize = (trunFlags & 0x000300) == 0 && sampleCount > 0 && entryBytes == sampleCount * 8u; var compactDurationSizeCto = (trunFlags & 0x000300) == 0 && sampleCount > 0 && entryBytes == sampleCount * 12u; var compactDurationSizeCtoTrailing = (trunFlags & 0x000300) == 0 && sampleCount > 0 && entryBytes == sampleCount * 12u + 4u; var compatibilityCtoBytes = compactDurationSizeCto || compactDurationSizeCtoTrailing;
            if (compactDurationSize) Logger.WarnMarkUp($"[PIPE-DIAG] fragment {t.Kind}: non-standard trun compatibility layout detected; perSampleBytes=8; interpreting duration+size"); else if (compatibilityCtoBytes) { var extra = compactDurationSizeCtoTrailing ? 4 : 0; Logger.WarnMarkUp($"[PIPE-DIAG] fragment {t.Kind}: non-standard trun compatibility layout detected; perSampleBytes={(entryBytes - extra) / sampleCount}; interpreting duration+size+cto{(extra > 0 ? $"; trailingBytes={extra}" : "")}"); if (extra > 0) Logger.WarnMarkUp($"[PIPE-DIAG] fragment {t.Kind}: compatibility trailing field=0x{ReadU32(trunPayload, trunPayload.Length - 4):X8}"); }
            Logger.WarnMarkUp($"[PIPE-DIAG] fragment {t.Kind}: dts={dts} defDur={defDur} defSize={defSize} defFlags=0x{defFlags:X8} dataPos={dataPos} entryBytes={entryBytes}"); var sampleRecordEnd = compactDurationSizeCtoTrailing ? trunPayload.Length - 4 : trunPayload.Length;
            for (uint i = 0; i < sampleCount; i++) { var dur = defDur; var size = defSize; var sf = defFlags; var cto = 0; if (compactDurationSize) { if (pos2 + 8 > sampleRecordEnd) { Logger.WarnMarkUp($"[PIPE-DIAG] fragment {t.Kind}: sample {i} missing compatibility duration/size"); break; } dur = ReadU32(trunPayload, pos2); pos2 += 4; size = ReadU32(trunPayload, pos2); pos2 += 4; } else if (compatibilityCtoBytes) { if (pos2 + 12 > sampleRecordEnd) { Logger.WarnMarkUp($"[PIPE-DIAG] fragment {t.Kind}: sample {i} missing compatibility duration/size/CTO"); break; } dur = ReadU32(trunPayload, pos2); pos2 += 4; size = ReadU32(trunPayload, pos2); pos2 += 4; cto = unchecked((int)ReadU32(trunPayload, pos2)); pos2 += 4; } else { if ((trunFlags & 0x000100) != 0) { if (pos2 + 4 > trunPayload.Length) break; dur = ReadU32(trunPayload, pos2); pos2 += 4; } if ((trunFlags & 0x000200) != 0) { if (pos2 + 4 > trunPayload.Length) break; size = ReadU32(trunPayload, pos2); pos2 += 4; } if ((trunFlags & 0x000400) != 0) { if (pos2 + 4 > trunPayload.Length) break; sf = ReadU32(trunPayload, pos2); pos2 += 4; } if ((trunFlags & 0x000800) != 0) { if (pos2 + 4 > trunPayload.Length) break; cto = unchecked((int)ReadU32(trunPayload, pos2)); pos2 += 4; } } if (size == 0) { Logger.WarnMarkUp($"[PIPE-DIAG] fragment {t.Kind}: sample {i} has zero size (flags=0x{trunFlags:X6}, trexSize={t.TrexDefaultSize})"); break; } if (dataPos + size > mdat.Length) { Logger.WarnMarkUp($"[PIPE-DIAG] fragment {t.Kind}: sample {i} exceeds mdat: pos={dataPos} size={size} mdat={mdat.Length}"); break; } if (i < 2) Logger.WarnMarkUp($"[PIPE-DIAG] fragment {t.Kind}: sample {i} duration={dur} size={size} cto={cto} dataPos={dataPos}"); result.Add(new Sample(mdat.AsSpan(checked((int)dataPos), checked((int)size)).ToArray(), dts, cto, (sf & 0x10000) == 0)); dts += dur; dataPos += size; }
        }
        return result;
    }

    private void WritePsi() { var pat = new byte[188]; Array.Fill(pat, (byte)0xFF); Header(pat, 0, true, ref pmtCc); pat[4] = 0; var ps = new byte[] { 0, 0xB0, 0x0D, 0, 1, 0xC1, 0, 0, 0, 1, 0xF0, 0, 0, 0, 0, 0 }; Crc(ps, 0, 12); Buffer.BlockCopy(ps, 0, pat, 5, 16); output.Write(pat); var pmt = new byte[188]; Array.Fill(pmt, (byte)0xFF); Header(pmt, PmtPid, true, ref pmtCc); pmt[4] = 0; var body = new byte[26]; body[0]=2; body[1]=0xB0; body[2]=0x17; body[3]=0; body[4]=1; body[5]=0xC1; body[6]=0; body[7]=0; body[8]=0xE1; body[9]=0; body[10]=0xF0; body[11]=0; body[12]=(byte)(video!.Codec==Codec.H265?0x24:0x1B); body[13]=0xE1; body[14]=0; body[15]=0xF0; body[16]=0; body[17]=0x0F; body[18]=0xE1; body[19]=1; body[20]=0xF0; body[21]=0; Crc(body,0,22); Buffer.BlockCopy(body,0,pmt,5,26); output.Write(pmt); Logger.WarnMarkUp("[PIPE-TS] WritePsi wrote PAT=188 bytes and PMT=188 bytes; totalTsBytes=376"); tsPacketsWritten += 2; tsBytesWritten += 376; }
    private void WritePes(byte[] payload, int pid, long pts, long dts, bool videoPes, ref int cc) { var h = new byte[dts == pts ? 14 : 19]; h[0]=0; h[1]=0; h[2]=1; h[3]=(byte)(videoPes?0xE0:0xC0); h[6]=(byte)(dts==pts?0x80:0xC0); h[7]=(byte)(dts==pts?5:10); WritePts(h.AsSpan(8), dts==pts?0x20:0x30, pts); if(dts!=pts) WritePts(h.AsSpan(13),0x10,dts); var pesLen = payload.Length + h.Length - 6; BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(4,2), pesLen <= 0xFFFF ? (ushort)pesLen : (ushort)0); var src = new byte[h.Length+payload.Length]; Buffer.BlockCopy(h,0,src,0,h.Length); Buffer.BlockCopy(payload,0,src,h.Length,payload.Length); var off=0; var first=true; var packetCount=0; var byteCount=0; while(off<src.Length) { var ts=new byte[188]; Array.Fill(ts,(byte)0xFF); ts[0]=0x47; ts[1]=(byte)(((pid>>8)&0x1F)|(first?0x40:0)); ts[2]=(byte)pid; var n=Math.Min(184,src.Length-off); var useAdapt=n<184; ts[3]=(byte)((useAdapt?0x30:0x10)|(cc++&15)); var p=4; if(useAdapt){var stuffing=182-n;ts[4]=(byte)(183-n);ts[5]=0;p=6+stuffing;} Buffer.BlockCopy(src,off,ts,p,n); off+=n; output.Write(ts); first=false; packetCount++; byteCount+=188; } pesWrites++; tsPacketsWritten += packetCount; tsBytesWritten += byteCount; if (pesWrites <= 5 || pesWrites % 100 == 0) Logger.WarnMarkUp($"[PIPE-TS] PES write #{pesWrites}: kind={(videoPes ? "video" : "audio")} pid=0x{pid:X} payload={payload.Length} pesBytes={src.Length} packets={packetCount} bytes={byteCount} pts={pts} dts={dts} cumulativeTsBytes={tsBytesWritten}"); }
    private static byte[] ConvertVideo(byte[] data, Track t){using var ms=new MemoryStream(data.Length+32);var p=0;while(p+t.NalLengthSize<=data.Length){var n=t.NalLengthSize switch{1=>data[p],2=>BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p,2)),3=>(data[p]<<16)|(data[p+1]<<8)|data[p+2],_=>checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p,4)))};p+=t.NalLengthSize;if(n<=0||p+n>data.Length)break;ms.Write(new byte[]{0,0,0,1});ms.Write(data,p,n);p+=n;}return ms.ToArray();}
    private static byte[] AddAdts(byte[] a,Track t){var len=a.Length+7;var h=new byte[len];h[0]=0xFF;h[1]=0xF1;h[2]=(byte)(Math.Clamp(t.AacProfile-1,0,3)<<6|t.AacFreq<<2|t.Channels>>2);h[3]=(byte)((t.Channels&3)<<6|(len>>11&3));h[4]=(byte)(len>>3);h[5]=(byte)((len&7)<<5|0x1F);h[6]=0xFC;Buffer.BlockCopy(a,0,h,7,a.Length);return h;}
    private static long Scale90(long x,uint scale)=>scale==0?x:x*90000L/(long)scale;
    private static void WritePts(Span<byte>d,int prefix,long v){v&=0x1FFFFFFFFL;d[0]=(byte)(prefix|(((v>>30)&7)<<1)|1);d[1]=(byte)(v>>22);d[2]=(byte)((((v>>15)&0x7F)<<1)|1);d[3]=(byte)(v>>7);d[4]=(byte)(((v&0x7F)<<1)|1);}
    private static void Header(byte[] b,int pid,bool pusi,ref int cc){b[0]=0x47;b[1]=(byte)(((pid>>8)&0x1F)|(pusi?0x40:0));b[2]=(byte)pid;b[3]=(byte)(0x10|(cc++&15));}
    private static void Crc(byte[] b,int start,int len){uint c=0xFFFFFFFF;for(int i=start;i<start+len;i++){c^=(uint)b[i]<<24;for(int j=0;j<8;j++)c=(c&0x80000000)!=0?(c<<1)^0x04C11DB7:c<<1;}BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(start+len,4),c);}
    private static int ReadU24(byte[]p,int i)=>(p[i]<<16)|(p[i+1]<<8)|p[i+2]; private static uint ReadU32(byte[]p,int i)=>BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(i,4));
    private static int Frequency(int i)=>i switch{0=>96000,1=>88200,2=>64000,3=>48000,4=>44100,5=>32000,6=>24000,7=>22050,8=>16000,9=>12000,10=>11025,11=>8000,12=>7350,_=>48000};
    private static byte[]? FindDescriptor(byte[]d,byte tag){if(d==null)return null;for(int i=0;i<d.Length-2;i++)if(d[i]==tag){int p=i+1,n=0;for(int j=0;j<4&&p<d.Length;j++){var q=d[p++];n=(n<<7)|(q&0x7F);if((q&0x80)==0)return p+n<=d.Length?d[p..(p+n)]:null;}}return null;}
    private static Box? FindBox(byte[]d,string t){foreach(var b in FindBoxes(d,t))return b;return null;}
    private static IEnumerable<Box> FindBoxes(byte[]d,string t){foreach(var b in Boxes(d)){if(b.Type==t)yield return b;foreach(var c in FindBoxes(b.Payload,t))yield return c;}}
    private static IEnumerable<Box> Boxes(byte[]d){int p=0;while(p+8<=d.Length){long s=BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p,4));var t=Encoding.ASCII.GetString(d,p+4,4);int h=8;if(s==1&&p+16<=d.Length){s=(long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(p+8,8));h=16;}if(s<h||p+s>d.Length)yield break;yield return new Box(t,d.AsSpan(p+h,checked((int)s-h)).ToArray());p+=checked((int)s);}}
    private readonly record struct Box(string Type,byte[] Payload); private readonly record struct Sample(byte[]Data,ulong Dts,int Cto,bool Sync);
    private enum Kind{Unknown,Video,Audio} private enum Codec{H264,H265}
    private sealed class Track{public Kind Kind;public Codec Codec;public uint TimeScale;public int NalLengthSize=4;public byte[]? Asc;public int AacProfile=2,AacFreq=4,SampleRate=44100,Channels=2;public uint TrackId;public uint DefaultSampleDescriptionIndex;public uint TrexDefaultDuration,TrexDefaultSize,TrexDefaultFlags;}
    private sealed class BoxReader{private readonly Stream s;public BoxReader(Stream s)=>this.s=s;public async Task<Box?> ReadAsync(){var h=new byte[8];var n=await ReadExact(h);if(n==0)return null;if(n<8)throw new EndOfStreamException();long z=BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(0,4));var t=Encoding.ASCII.GetString(h,4,4);int hs=8;if(z==1){var x=new byte[8];if(await ReadExact(x)!=8)throw new EndOfStreamException();z=(long)BinaryPrimitives.ReadUInt64BigEndian(x);hs=16;}if(z<hs||z>int.MaxValue)throw new InvalidDataException();var p=new byte[(int)z-hs];if(await ReadExact(p)!=p.Length)throw new EndOfStreamException();return new Box(t,p);}private async Task<int>ReadExact(byte[]b){int n=0;while(n<b.Length){var k=await s.ReadAsync(b.AsMemory(n));if(k==0)break;n+=k;}return n;}}
}