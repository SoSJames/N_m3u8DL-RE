using N_m3u8DL_RE.Common.Log;
using Spectre.Console;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using N_m3u8DL_RE.Config;

namespace N_m3u8DL_RE.Util;

internal static class PipeUtil
{
    private const string StreamPipeOutputEnvironmentVariable = "N_M3U8_STREAM_PIPE_OUTPUT";

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

            // FFmpeg-free streaming mode for a single already-muxed stream.
            // The destination is expected to be a FIFO/pipe or another streaming sink.
            // This deliberately refuses multi-track input because copying separate audio
            // and video pipes would not produce a valid muxed MPEG-TS stream.
            var streamOutput = Environment.GetEnvironmentVariable(StreamPipeOutputEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(streamOutput))
            {
                if (pipeNames.Length != 1)
                {
                    Logger.ErrorMarkUp($"[red]{StreamPipeOutputEnvironmentVariable} requires exactly one selected non-subtitle stream; refusing to invoke FFmpeg.[/]");
                    return false;
                }

                Logger.InfoMarkUp($"[deepskyblue1]FFmpeg-free live pipe output:[/] {streamOutput.EscapeMarkup()}");
                return await ForwardPipeAsync(pipeNames[0], streamOutput);
            }

            return StartPipeMux(binary, pipeNames, outputPath);
        });
    }

    /// <summary>
    /// Forward one N_m3u8 pipe directly to a streaming destination without FFmpeg.
    /// The destination must be a FIFO/pipe/socket-like sink; it is never treated as a
    /// regular growing recording file by this method.
    /// </summary>
    private static async Task<bool> ForwardPipeAsync(string pipeName, string outputPath)
    {
        try
        {
            var pipePath = OperatingSystem.IsWindows()
                ? $"\\\\.\\pipe\\{pipeName}"
                : Path.Combine(Path.GetTempPath(), pipeName);

            await using var input = new FileStream(
                pipePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 1024 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using var output = new FileStream(
                outputPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 1024 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            await input.CopyToAsync(output);
            await output.FlushAsync();
            return true;
        }
        catch (Exception ex)
        {
            Logger.ErrorMarkUp($"[red]Live pipe forwarding failed: {ex.Message.EscapeMarkup()}[/]");
            return false;
        }
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
                // command.Append($" -i \"unix://{Path.Combine(Path.GetTempPath(), $"CoreFxPipe_{item}")}\" ");
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
        // p.StartInfo.Environment.Add("FFREPORT", "file=ffreport.log:level=42");
        p.Start();
        p.WaitForExit();

        return p.ExitCode == 0;
    }
}