using N_m3u8DL_RE.Common.Log;
using System.Diagnostics;

namespace N_m3u8DL_RE.Util;

internal sealed class ShakaLivePipeMux : IDisposable
{
    private readonly string _binary;
    private readonly string[] _pipeNames;
    private readonly string _outputPath;
    private Process? _process;

    public ShakaLivePipeMux(string binary, string[] pipeNames, string outputPath)
    {
        _binary = binary;
        _pipeNames = pipeNames;
        _outputPath = outputPath;
    }

    public bool Start()
    {
        if (!OperatingSystem.IsLinux())
        {
            Logger.ErrorMarkUp("[red]Experimental Shaka live pipe mux currently requires Linux.[/]");
            return false;
        }

        if (_pipeNames.Length < 2)
        {
            Logger.ErrorMarkUp("[red]Experimental Shaka live pipe mux requires video and audio pipes.[/]");
            return false;
        }

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(_outputPath))!;
        var baseName = Path.GetFileNameWithoutExtension(_outputPath);
        var root = Path.Combine(baseDir, baseName + "-shaka-hls");
        Directory.CreateDirectory(Path.Combine(root, "video"));
        Directory.CreateDirectory(Path.Combine(root, "audio"));

        var pipeDir = OtherUtil.GetEnvironmentVariable(EnvConfigKey.ReLivePipeTmpDir, Path.GetTempPath());
        var videoPipe = Path.Combine(pipeDir, _pipeNames[0]);
        var audioPipe = Path.Combine(pipeDir, _pipeNames[1]);

        var video = $"in={videoPipe},stream=video,init_segment={Path.Combine(root, "video", "init.mp4")},segment_template={Path.Combine(root, "video", "$Number$.m4s")},playlist_name=video/video.m3u8";
        var audio = $"in={audioPipe},stream=audio,init_segment={Path.Combine(root, "audio", "init.mp4")},segment_template={Path.Combine(root, "audio", "$Number$.m4s")},playlist_name=audio/audio.m3u8,hls_group_id=audio,hls_name=English,lang=en";
        var master = Path.Combine(root, "master.m3u8");

        var psi = new ProcessStartInfo
        {
            FileName = _binary,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add(video);
        psi.ArgumentList.Add(audio);
        psi.ArgumentList.Add("--hls_master_playlist_output");
        psi.ArgumentList.Add(master);
        psi.ArgumentList.Add("--hls_playlist_type");
        psi.ArgumentList.Add("LIVE");
        psi.ArgumentList.Add("--segment_duration");
        psi.ArgumentList.Add("2.002");
        psi.ArgumentList.Add("--fragment_duration");
        psi.ArgumentList.Add("2.002");
        psi.ArgumentList.Add("--time_shift_buffer_depth");
        psi.ArgumentList.Add("24");
        psi.ArgumentList.Add("--preserved_segments_outside_live_window");
        psi.ArgumentList.Add("24");
        psi.ArgumentList.Add("--suggested_presentation_delay");
        psi.ArgumentList.Add("8");
        psi.ArgumentList.Add("--default_language");
        psi.ArgumentList.Add("en");
        psi.ArgumentList.Add("--io_block_size");
        psi.ArgumentList.Add("65536");

        Logger.WarnMarkUp($"[deepskyblue1]Experimental Shaka live packager:[/] {_binary} -> {master}");
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Logger.DebugMarkUp($"[grey][Shaka] {e.Data.EscapeMarkup()}[/]");
        };

        if (!_process.Start()) return false;
        _process.BeginErrorReadLine();
        return true;
    }

    public void Dispose()
    {
        if (_process == null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch { }
        _process.Dispose();
        _process = null;
    }
}
