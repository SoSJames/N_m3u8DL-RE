using System.Buffers.Binary;
using System.Text;
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

    private NativeFmp4TsMuxer(Stream output) => this.output = output;

    public static async Task<bool> RunAsync(string[] pipeNames, string outputPath)
    {
        if (pipeNames.Length != 2)
        {
            Logger.ErrorMarkUp("[red]Native mux requires exactly two non-subtitle streams (video + audio).[/]");
            return false;
        }
        try
        {
            await using var p0 = OpenPipe(pipeNames[0]);
            await using var p1 = OpenPipe(pipeNames[1]);
            // StreamRelay supplies a FIFO here. FileMode.Open is intentional: this class
            // never creates or truncates a regular recording file.
            await using var dst = new FileStream(outputPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var mux = new NativeFmp4TsMuxer(dst);
            await Task.WhenAll(mux.ReadPipeAsync(p0, 0), mux.ReadPipeAsync(p1, 1));
            await dst.FlushAsync();
            return mux.psiWritten;
        }
        catch (Exception ex)
        {
            Logger.ErrorMarkUp($"[red]Native fMP4 mux failed: {ex.Message.EscapeMarkup()}[/]");
            return false;
        }
    }

    private static Stream OpenPipe(string name) => OperatingSystem.IsWindows()
        ? new FileStream($"\\\\.\\pipe\\{name}", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.Asynchronous)
        : new FileStream(Path.Combine(Path.GetTempPath(), name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.Asynchronous);

    private async Task ReadPipeAsync(Stream pipe, int pipeIndex)
    {
        var r = new BoxReader(pipe);
        Track? track = null;
        while (true)
        {
            var box = await r.ReadAsync();
            if (box == null) break;
            if (box.Value.Type == "moov")
            {
                track = ParseInit(box.Value.Payload);
                if (track.Kind == Kind.Video) video = track; else audio = track;
                continue;
            }
            if (box.Value.Type != "moof") continue;
            var mdat = await r.ReadAsync();
            if (mdat == null || mdat.Value.Type != "mdat") throw new InvalidDataException("moof is not followed by mdat");
            if (track == null) continue;
            foreach (var sample in ParseFragment(box.Value.Payload, mdat.Value.Payload, track))
                await EmitAsync(track, sample);
        }
    }

    private async Task EmitAsync(Track t, Sample s)
    {
        while (video == null || audio == null) await Task.Delay(5);
        lock (gate)
        {
            if (!psiWritten) { WritePsi(); psiWritten = true; }
            var pts = Scale90((long)s.Dts + s.Cto, t.TimeScale);
            var dts = Scale90((long)s.Dts, t.TimeScale);
            if (t.Kind == Kind.Video) WritePes(ConvertVideo(s.Data, t), VideoPid, pts, dts, true, ref videoCc);
            else WritePes(AddAdts(s.Data, t), AudioPid, pts, pts, false, ref audioCc);
        }
        await Task.CompletedTask;
    }

    private Track ParseInit(byte[] moov)
    {
        foreach (var trak in FindBoxes(moov, "trak"))
        {
            var h = FindBox(trak.Payload, "hdlr");
            if (h == null || h.Value.Payload.Length < 12) continue;
            var hp = h.Value.Payload;
            var handler = Encoding.ASCII.GetString(hp, 8, 4);
            var t = new Track { Kind = handler == "vide" ? Kind.Video : handler == "soun" ? Kind.Audio : Kind.Unknown };
            if (t.Kind == Kind.Unknown) continue;
            var mdhd = FindBox(trak.Payload, "mdhd");
            if (mdhd != null)
            {
                var p = mdhd.Value.Payload;
                if (p.Length >= 20 && p[0] == 1) t.TimeScale = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(20, 4));
                else if (p.Length >= 16) t.TimeScale = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12, 4));
            }
            if (t.TimeScale == 0) t.TimeScale = t.Kind == Kind.Audio ? 48000u : 90000u;
            if (t.Kind == Kind.Video)
            {
                var avc = FindBox(trak.Payload, "avcC");
                var hvc = FindBox(trak.Payload, "hvcC");
                if (avc != null && avc.Value.Payload.Length >= 5) { t.Codec = Codec.H264; t.NalLengthSize = (avc.Value.Payload[4] & 3) + 1; }
                else if (hvc != null && hvc.Value.Payload.Length >= 22) { t.Codec = Codec.H265; t.NalLengthSize = (hvc.Value.Payload[21] & 3) + 1; }
                else throw new InvalidDataException("Video init has neither avcC nor hvcC");
            }
            else
            {
                var esds = FindBox(trak.Payload, "esds");
                var asc = esds == null ? null : FindDescriptor(esds.Value.Payload, 0x05);
                if (asc == null || asc.Length < 2) throw new InvalidDataException("AAC init has no AudioSpecificConfig");
                t.Asc = asc;
                var a0 = asc[0]; var a1 = asc[1];
                t.AacProfile = Math.Max(1, a0 >> 3);
                t.AacFreq = ((a0 & 7) << 1) | (a1 >> 7);
                t.SampleRate = Frequency(t.AacFreq);
                t.Channels = (a1 >> 3) & 15; if (t.Channels == 0) t.Channels = 2;
            }
            return t;
        }
        throw new InvalidDataException("No supported track in moov");
    }

    private static List<Sample> ParseFragment(byte[] moof, byte[] mdat, Track t)
    {
        var result = new List<Sample>();
        foreach (var traf in FindBoxes(moof, "traf"))
        {
            var tfhd = FindBox(traf.Payload, "tfhd");
            var tfdt = FindBox(traf.Payload, "tfdt");
            var trun = FindBox(traf.Payload, "trun");
            if (trun == null) continue;
            ulong dts = 0;
            if (tfdt != null) dts = tfdt.Value.Payload[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(tfdt.Value.Payload.AsSpan(4, 8)) : BinaryPrimitives.ReadUInt32BigEndian(tfdt.Value.Payload.AsSpan(4, 4));
            uint defDur = 0, defSize = 0;
            if (tfhd != null)
            {
                var p = tfhd.Value.Payload; var flags = ReadU24(p, 0); var pos = 8;
                if ((flags & 1) != 0) pos += 8; if ((flags & 2) != 0) pos += 4;
                if ((flags & 8) != 0 && pos + 4 <= p.Length) { defDur = ReadU32(p, pos); pos += 4; }
                if ((flags & 16) != 0 && pos + 4 <= p.Length) defSize = ReadU32(p, pos);
            }
            var p2 = trun.Value.Payload; if (p2.Length < 8) continue;
            var flags2 = ReadU24(p2, 0); var count = ReadU32(p2, 4); var pos2 = 8;
            if ((flags2 & 1) != 0) pos2 += 4; if ((flags2 & 4) != 0) pos2 += 4;
            var dataPos = 0;
            for (uint i = 0; i < count && pos2 <= p2.Length; i++)
            {
                var dur = defDur; var size = defSize; var sf = 0u; var cto = 0;
                if ((flags2 & 0x100) != 0) { dur = ReadU32(p2, pos2); pos2 += 4; }
                if ((flags2 & 0x200) != 0) { size = ReadU32(p2, pos2); pos2 += 4; }
                if ((flags2 & 0x400) != 0) { sf = ReadU32(p2, pos2); pos2 += 4; }
                if ((flags2 & 0x800) != 0) { cto = (int)ReadU32(p2, pos2); pos2 += 4; }
                if (size == 0 || dataPos + size > mdat.Length) break;
                result.Add(new Sample(mdat.AsSpan(dataPos, checked((int)size)).ToArray(), dts, cto, (sf & 0x10000) == 0));
                dts += dur; dataPos += checked((int)size);
            }
        }
        return result;
    }

    private void WritePsi()
    {
        var pat = new byte[188]; Array.Fill(pat, (byte)0xFF); Header(pat, 0, true, ref pmtCc); pat[4] = 0;
        var ps = new byte[] { 0, 0xB0, 0x0D, 0, 1, 0xC1, 0, 0, 0, 1, 0xF0, 0, 0, 0, 0, 0 };
        Crc(ps, 0, 12); Buffer.BlockCopy(ps, 0, pat, 5, 16); output.Write(pat);

        var pmt = new byte[188]; Array.Fill(pmt, (byte)0xFF); Header(pmt, PmtPid, true, ref pmtCc); pmt[4] = 0;
        var body = new byte[26]; body[0]=2; body[1]=0xB0; body[2]=0x17; body[3]=0; body[4]=1; body[5]=0xC1; body[6]=0; body[7]=0;
        body[8]=0xE1; body[9]=0; body[10]=0xF0; body[11]=0;
        body[12]=(byte)(video!.Codec==Codec.H265?0x24:0x1B); body[13]=0xE1; body[14]=0; body[15]=0xF0; body[16]=0;
        body[17]=0x0F; body[18]=0xE1; body[19]=1; body[20]=0xF0; body[21]=0; Crc(body,0,22); Buffer.BlockCopy(body,0,pmt,5,26); output.Write(pmt);
    }

    private void WritePes(byte[] payload, int pid, long pts, long dts, bool videoPes, ref int cc)
    {
        var h = new byte[dts == pts ? 14 : 19]; h[0]=0; h[1]=0; h[2]=1; h[3]=(byte)(videoPes?0xE0:0xC0); h[6]=(byte)(dts==pts?0x80:0xC0); h[7]=(byte)(dts==pts?5:10); WritePts(h.AsSpan(8), dts==pts?0x20:0x30, pts); if(dts!=pts) WritePts(h.AsSpan(13),0x10,dts);
        var pesLen = payload.Length + h.Length - 6; BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(4,2), pesLen <= 0xFFFF ? (ushort)pesLen : (ushort)0);
        var src = new byte[h.Length+payload.Length]; Buffer.BlockCopy(h,0,src,0,h.Length); Buffer.BlockCopy(payload,0,src,h.Length,payload.Length); var off=0; var first=true;
        while(off<src.Length)
        {
            var ts=new byte[188]; Array.Fill(ts,(byte)0xFF); ts[0]=0x47; ts[1]=(byte)(((pid>>8)&0x1F)|(first?0x40:0)); ts[2]=(byte)pid;
            var n=Math.Min(184,src.Length-off); var useAdapt=n<184;
            ts[3]=(byte)((useAdapt?0x30:0x10)|(cc++&15)); var p=4;
            if(useAdapt){var stuffing=182-n;ts[4]=(byte)(183-n);ts[5]=0;p=6+stuffing;}
            Buffer.BlockCopy(src,off,ts,p,n); off+=n; output.Write(ts); first=false;
        }
    }

    private static byte[] ConvertVideo(byte[] data, Track t){using var ms=new MemoryStream(data.Length+32);var p=0;while(p+t.NalLengthSize<=data.Length){var n=t.NalLengthSize switch{1=>data[p],2=>BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p,2)),3=>(data[p]<<16)|(data[p+1]<<8)|data[p+2],_=>checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p,4)))};p+=t.NalLengthSize;if(n<=0||p+n>data.Length)break;ms.Write(new byte[]{0,0,0,1});ms.Write(data,p,n);p+=n;}return ms.ToArray();}
    private static byte[] AddAdts(byte[] a,Track t){var len=a.Length+7;var h=new byte[len];h[0]=0xFF;h[1]=0xF1;h[2]=(byte)(Math.Clamp(t.AacProfile-1,0,3)<<6|t.AacFreq<<2|t.Channels>>2);h[3]=(byte)((t.Channels&3)<<6|(len>>11&3));h[4]=(byte)(len>>3);h[5]=(byte)((len&7)<<5|0x1F);h[6]=0xFC;Buffer.BlockCopy(a,0,h,7,a.Length);return h;}
    private static long Scale90(long x,uint scale)=>scale==0?x:x*90000L/(long)scale;
    private static void WritePts(Span<byte>d,int prefix,long v){v&=0x1FFFFFFFFL;d[0]=(byte)(prefix|(((v>>30)&7)<<1)|1);d[1]=(byte)(v>>22);d[2]=(byte)((((v>>15)&0x7F)<<1)|1);d[3]=(byte)(v>>7);d[4]=(byte)(((v&0x7F)<<1)|1);}
    private static void Header(byte[] b,int pid,bool pusi,ref int cc){b[0]=0x47;b[1]=(byte)(((pid>>8)&0x1F)|(pusi?0x40:0));b[2]=(byte)pid;b[3]=(byte)(0x10|(cc++&15));}
    private static void Crc(byte[] b,int start,int len){uint c=0xFFFFFFFF;for(int i=start;i<start+len;i++){c^=(uint)b[i]<<24;for(int j=0;j<8;j++)c=(c&0x80000000)!=0?(c<<1)^0x04C11DB7:c<<1;}BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(start+len,4),c);}
    private static int ReadU24(byte[]p,int i)=>(p[i]<<16)|(p[i+1]<<8)|p[i+2]; private static uint ReadU32(byte[]p,int i)=>BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(i,4));
    private static int Frequency(int i)=>i switch{0=>96000,1=>88200,2=>64000,3=>48000,4=>44100,5=>32000,6=>24000,7=>22050,8=>16000,9=>12000,10=>11025,11=>8000,12=>7350,_=>48000};
    private static byte[]? FindDescriptor(byte[]d,byte tag){for(int i=0;i<d.Length-2;i++)if(d[i]==tag){int p=i+1,n=0;for(int j=0;j<4&&p<d.Length;j++){var q=d[p++];n=(n<<7)|(q&0x7F);if((q&0x80)==0)return p+n<=d.Length?d[p..(p+n)]:null;}}return null;}
    private static Box? FindBox(byte[]d,string t)=>FindBoxes(d,t).FirstOrDefault();
    private static IEnumerable<Box> FindBoxes(byte[]d,string t){foreach(var b in Boxes(d)){if(b.Type==t)yield return b;foreach(var c in FindBoxes(b.Payload,t))yield return c;}}
    private static IEnumerable<Box> Boxes(byte[]d){int p=0;while(p+8<=d.Length){long s=BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p,4));var t=Encoding.ASCII.GetString(d,p+4,4);int h=8;if(s==1&&p+16<=d.Length){s=(long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(p+8,8));h=16;}if(s<h||p+s>d.Length)yield break;yield return new Box(t,d.AsSpan(p+h,checked((int)s-h)).ToArray());p+=checked((int)s);}}
    private readonly record struct Box(string Type,byte[] Payload); private readonly record struct Sample(byte[]Data,ulong Dts,int Cto,bool Sync);
    private enum Kind{Unknown,Video,Audio} private enum Codec{H264,H265}
    private sealed class Track{public Kind Kind;public Codec Codec;public uint TimeScale;public int NalLengthSize=4;public byte[]? Asc;public int AacProfile=2,AacFreq=4,SampleRate=44100,Channels=2;}
    private sealed class BoxReader{private readonly Stream s;public BoxReader(Stream s)=>this.s=s;public async Task<Box?> ReadAsync(){var h=new byte[8];var n=await ReadExact(h);if(n==0)return null;if(n<8)throw new EndOfStreamException();long z=BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(0,4));var t=Encoding.ASCII.GetString(h,4,4);int hs=8;if(z==1){var x=new byte[8];if(await ReadExact(x)!=8)throw new EndOfStreamException();z=(long)BinaryPrimitives.ReadUInt64BigEndian(x);hs=16;}if(z<hs||z>int.MaxValue)throw new InvalidDataException();var p=new byte[(int)z-hs];if(await ReadExact(p)!=p.Length)throw new EndOfStreamException();return new Box(t,p);}private async Task<int>ReadExact(byte[]b){int n=0;while(n<b.Length){var k=await s.ReadAsync(b.AsMemory(n));if(k==0)break;n+=k;}return n;}}
}
