using N_m3u8DL_RE.Common.Log;
using Spectre.Console;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using N_m3u8DL_RE.Config;

namespace N_m3u8DL_RE.Util;

internal static class PipeUtil
{
    public static Stream CreatePipe(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut);
        }

        var path = Path.Combine(Path.GetTempPath(), pipeName);
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo()
        {
            FileName = "mkfifo",
            Arguments = path,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        p.Start();
        p.WaitForExit();
        Thread.Sleep(200);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
    }

    public static async Task<bool> StartPipeMuxAsync(string binary, string[] pipeNames, string outputPath)
    {
        return await Task.Run(async () =>
        {
            await Task.Delay(1000);

            // Experimental live Shaka mode. The normal FFmpeg pipe path remains
            // unchanged unless this environment variable is explicitly set.
            var shakaBinary = Environment.GetEnvironmentVariable("N_M3U8DL_RE_LIVE_SHAKA_PACKAGER");
            if (!string.IsNullOrWhiteSpace(shakaBinary))
                return await StartShakaLiveAsync(shakaBinary, pipeNames, outputPath);

            return StartPipeMux(binary, pipeNames, outputPath);
        });
    }

    private static async Task<bool> StartShakaLiveAsync(string binary, string[] pipeNames, string outputPath)
    {
        if (pipeNames.Length == 0 || pipeNames.Length > 2)
        {
            Logger.ErrorMarkUp("Experimental Shaka live mode supports one video pipe or one video + one audio pipe.");
            return false;
        }

        var outputDir = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(outputPath) + ".hls");
        Directory.CreateDirectory(outputDir);
        var videoDir = Path.Combine(outputDir, "video");
        Directory.CreateDirectory(videoDir);

        var audioDir = Path.Combine(outputDir, "audio");
        if (pipeNames.Length == 2)
            Directory.CreateDirectory(audioDir);

        var descriptors = new List<string>();
        var videoPipe = GetPipePath(pipeNames[0]);
        descriptors.Add(
            $"in=\"{videoPipe}\",stream=video,init_segment=\"{Path.Combine(videoDir, "init.mp4")}\",segment_template=\"{Path.Combine(videoDir, "$Number$.m4s")}\",playlist_name=\"video.m3u8\"");

        if (pipeNames.Length == 2)
        {
            var audioPipe = GetPipePath(pipeNames[1]);
            descriptors.Add(
                $"in=\"{audioPipe}\",stream=audio,language=en,hls_name=English,init_segment=\"{Path.Combine(audioDir, "init.mp4")}\",segment_template=\"{Path.Combine(audioDir, "$Number$.m4s")}\",playlist_name=\"audio.m3u8\",hls_group_id=audio");
        }

        var master = Path.Combine(outputDir, "master.m3u8");
        var args = new StringBuilder();
        foreach (var descriptor in descriptors)
            args.Append($" \"{descriptor}\"");
        args.Append($" --hls_master_playlist_output \"{master}\"");
        args.Append(" --hls_playlist_type LIVE");
        args.Append(" --segment_duration 2.002");
        args.Append(" --fragment_duration 2.002");
        args.Append(" --time_shift_buffer_depth 24");
        args.Append(" --preserved_segments_outside_live_window 24");
        args.Append(" --suggested_presentation_delay 8");
        args.Append(" --default_language en");
        args.Append(" --io_block_size 65536");

        Logger.WarnMarkUp($"[deepskyblue1]Experimental Shaka live mode[/]");
        Logger.InfoMarkUp($"HLS output: [cyan]{master.EscapeMarkup()}[/]");
        Logger.DebugMarkUp($"[grey]{binary.EscapeMarkup()} {args.ToString().EscapeMarkup()}[/]");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo()
        {
            WorkingDirectory = Environment.CurrentDirectory,
            FileName = binary,
            Arguments = args.ToString(),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        process.Start();
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    Logger.DebugMarkUp($"[grey][Shaka] {line.EscapeMarkup()}[/]");
            }
        });
        _ = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    Logger.DebugMarkUp($"[grey][Shaka] {line.EscapeMarkup()}[/]");
            }
        });

        await process.WaitForExitAsync();
        Logger.WarnMarkUp($"Shaka Packager exited with code {process.ExitCode}");
        return process.ExitCode == 0;
    }

    private static string GetPipePath(string pipeName)
    {
        if (OperatingSystem.IsWindows())
            return $"\\\\.\\pipe\\{pipeName}";
        return Path.Combine(Path.GetTempPath(), pipeName);
    }

    public static bool StartPipeMux(string binary, string[] pipeNames, string outputPath)
    {
        var dateString = DateTime.Now.ToString("o");
        var command = new StringBuilder("-y -fflags +genpts -loglevel quiet ");

        var customDest = OtherUtil.GetEnvironmentVariable(EnvConfigKey.ReLivePipeOptions);
        var pipeDir = OtherUtil.GetEnvironmentVariable(EnvConfigKey.ReLivePipeTmpDir, Path.GetTempPath());

        if (!string.IsNullOrEmpty(customDest))
        {
            command.Append(" -re ");
        }

        foreach (var item in pipeNames)
        {
            if (OperatingSystem.IsWindows())
                command.Append($" -i \"\\\\.\\pipe\\{item}\" ");
            else
                command.Append($" -i \"{Path.Combine(pipeDir, item)}\" ");
        }

        for (var i = 0; i < pipeNames.Length; i++)
        {
            command.Append($" -map {i} ");
        }

        command.Append(" -strict unofficial -c copy ");
        command.Append($" -metadata date=\"{dateString}\" ");
        command.Append($" -ignore_unknown -copy_unknown ");

        if (!string.IsNullOrEmpty(customDest))
        {
            if (customDest.Trim().StartsWith('-'))
                command.Append(customDest);
            else
                command.Append($" -f mpegts -shortest \"{customDest}\"");
            Logger.WarnMarkUp($"[deepskyblue1]{command.ToString().EscapeMarkup()}[/]");
        }
        else
        {
            command.Append($" -f mpegts -shortest \"{outputPath}\"");
        }

        using var p = new Process();
        p.StartInfo = new ProcessStartInfo()
        {
            WorkingDirectory = Environment.CurrentDirectory,
            FileName = binary,
            Arguments = command.ToString(),
            CreateNoWindow = true,
            UseShellExecute = false
        };
        p.Start();
        p.WaitForExit();

        return p.ExitCode == 0;
    }
}