using N_m3u8DL_RE.Common.Log;
using Spectre.Console;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using N_m3u8DL_RE.Config;

namespace N_m3u8DL_RE.Util;

internal static class PipeUtil
{
    private const string StreamPipeOutputEnvironmentVariable = "N_M3U8_STREAM_PIPE_OUTPUT";

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string pathname, uint mode);

    public static Stream CreatePipe(string pipeName)
    {
        Logger.WarnMarkUp($"[PIPE-TRACE] CreatePipe called name={pipeName.EscapeMarkup()} thread={Environment.CurrentManagedThreadId}");
        Logger.WarnMarkUp($"[PIPE-TRACE] CreatePipe caller={new StackTrace(1, true).ToString().Replace(Environment.NewLine, " | ").EscapeMarkup()}");

        if (OperatingSystem.IsWindows())
        {
            Logger.InfoMarkUp($"[yellow]PIPE create (Windows): {pipeName.EscapeMarkup()}[/]");
            var stream = new NamedPipeServerStream(pipeName, PipeDirection.InOut);
            Logger.InfoMarkUp($"[yellow]PIPE Windows stream opened: {pipeName.EscapeMarkup()}[/]");
            return stream;
        }

        var path = Path.Combine(Path.GetTempPath(), pipeName);
        Logger.InfoMarkUp($"[yellow]PIPE create (FIFO) begin: {path.EscapeMarkup()}[/]");

        try
        {
            if (File.Exists(path))
            {
                Logger.WarnMarkUp($"[PIPE-TRACE] FIFO path already exists; removing stale path: {path.EscapeMarkup()}");
                File.Delete(path);
            }

            Logger.InfoMarkUp($"[yellow]PIPE mkfifo begin: {path.EscapeMarkup()}[/]");
            var rc = mkfifo(path, 0x180u); // 0600
            var errno = Marshal.GetLastWin32Error();
            Logger.InfoMarkUp($"[yellow]PIPE mkfifo returned rc={rc} errno={errno}[/]");
            if (rc != 0)
                throw new IOException($"mkfifo failed for '{path}' with errno {errno}");

            var attributes = File.GetAttributes(path);
            Logger.InfoMarkUp($"[yellow]PIPE FIFO exists/type={attributes}; opening read/write[/]");

            // Open an existing FIFO. ReadWrite prevents the open from waiting for a
            // separate reader/writer, while FileMode.Open avoids silently replacing
            // a FIFO with an ordinary file.
            var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Logger.InfoMarkUp($"[yellow]PIPE opened: {path.EscapeMarkup()}[/]");
            return stream;
        }
        catch (Exception ex)
        {
            Logger.ErrorMarkUp($"[PIPE-TRACE] CreatePipe FAILED path={path.EscapeMarkup()} type={ex.GetType().Name} message={ex.Message.EscapeMarkup()}");
            throw;
        }
    }

    public static async Task<bool> StartPipeMuxAsync(string binary, string[] pipeNames, string outputPath)
    {
        return await Task.Run(async () =>
        {
            Logger.InfoMarkUp($"[yellow]PIPE mux requested: streams={pipeNames.Length}; output={outputPath.EscapeMarkup()}[/]");
            foreach (var pipe in pipeNames)
                Logger.InfoMarkUp($"[yellow]PIPE mux input: {pipe.EscapeMarkup()}[/]");

            await Task.Delay(1000);
            var streamOutput = Environment.GetEnvironmentVariable(StreamPipeOutputEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(streamOutput))
            {
                Logger.InfoMarkUp($"[deepskyblue1]FFmpeg-free native MPEG-TS pipe output:[/] {streamOutput.EscapeMarkup()}");
                Logger.InfoMarkUp("[deepskyblue1]Starting native fMP4 -> MPEG-TS muxer.[/]");
                var result = await NativeFmp4TsMuxer.RunAsync(pipeNames, streamOutput);
                Logger.InfoMarkUp($"[deepskyblue1]Native fMP4 -> MPEG-TS muxer returned: {result}[/]");
                return result;
            }
            Logger.InfoMarkUp("[yellow]N_M3U8_STREAM_PIPE_OUTPUT is not set; using legacy FFmpeg pipe mux.[/]");
            return StartPipeMux(binary, pipeNames, outputPath);
        });
    }

    public static bool StartPipeMux(string binary, string[] pipeNames, string outputPath)
    {
        var dateString = DateTime.Now.ToString("o");
        var command = new StringBuilder("-y -fflags +genpts -loglevel quiet ");
        var customDest = OtherUtil.GetEnvironmentVariable(EnvConfigKey.ReLivePipeOptions);
        var pipeDir = OtherUtil.GetEnvironmentVariable(EnvConfigKey.ReLivePipeTmpDir, Path.GetTempPath());
        if (!string.IsNullOrEmpty(customDest)) command.Append(" -re ");
        foreach (var item in pipeNames)
        {
            if (OperatingSystem.IsWindows()) command.Append($" -i \\\"\\\\.\\pipe\\{item}\\\" ");
            else command.Append($" -i \\\"{Path.Combine(pipeDir, item)}\\\" ");
        }
        for (var i = 0; i < pipeNames.Length; i++) command.Append($" -map {i} ");
        command.Append(" -strict unofficial -c copy ");
        command.Append($" -metadata date=\\\"{dateString}\\\" -ignore_unknown -copy_unknown ");
        if (!string.IsNullOrEmpty(customDest))
        {
            if (customDest.Trim().StartsWith('-')) command.Append(customDest);
            else command.Append($" -f mpegts -shortest \\\"{customDest}\\\"");
            Logger.WarnMarkUp($"[deepskyblue1]{command.ToString().EscapeMarkup()}[/]");
        }
        else command.Append($" -f mpegts -shortest \\\"{outputPath}\\\"");

        using var p = new Process { StartInfo = new ProcessStartInfo {
            WorkingDirectory = Environment.CurrentDirectory, FileName = binary, Arguments = command.ToString(),
            CreateNoWindow = true, UseShellExecute = false } };
        p.Start(); p.WaitForExit();
        return p.ExitCode == 0;
    }
}