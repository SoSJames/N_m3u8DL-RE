using N_m3u8DL_RE.Common.Log;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using N_m3u8DL_RE.Config;

namespace N_m3u8DL_RE.Util;

internal static class PipeUtil
{
    private const string StreamPipeOutputEnvironmentVariable = "N_M3U8_STREAM_PIPE_OUTPUT";
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> NativePipeRegistries = new();
    private static readonly ConcurrentDictionary<string, Task<bool>> NativeMuxTasks = new();

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

            RegisterNativePipeAndMaybeStartMux(pipeName);
            return stream;
        }
        catch (Exception ex)
        {
            Logger.ErrorMarkUp($"[PIPE-TRACE] CreatePipe FAILED path={path.EscapeMarkup()} type={ex.GetType().Name} message={ex.Message.EscapeMarkup()}");
            throw;
        }
    }

    private static void RegisterNativePipeAndMaybeStartMux(string pipeName)
    {
        var streamOutput = Environment.GetEnvironmentVariable(StreamPipeOutputEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(streamOutput))
            return;

        var registry = NativePipeRegistries.GetOrAdd(streamOutput, _ => new ConcurrentDictionary<string, byte>());
        registry.TryAdd(pipeName, 0);
        Logger.InfoMarkUp($"[yellow]PIPE registration: {pipeName.EscapeMarkup()} ({registry.Count}/2)[/]");

        // The native muxer is deliberately fixed to exactly two inputs: video + audio.
        // Start it as soon as both pipes exist instead of depending on the selected-stream
        // count, which may include an unpiped/extra track and otherwise leaves the muxer
        // never started.
        if (registry.Count != 2)
            return;

        var names = registry.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Logger.WarnMarkUp($"[deepskyblue1]PIPE registration complete: {names.Length}/2; starting native mux.[/]");
        _ = StartPipeMuxAsync(string.Empty, names, streamOutput);
    }

    public static Task<bool> StartPipeMuxAsync(string binary, string[] pipeNames, string outputPath)
    {
        Logger.InfoMarkUp($"[yellow]PIPE mux requested: streams={pipeNames.Length}; output={outputPath.EscapeMarkup()}[/]");
        foreach (var pipe in pipeNames)
            Logger.InfoMarkUp($"[yellow]PIPE mux input: {pipe.EscapeMarkup()}[/]");

        var streamOutput = Environment.GetEnvironmentVariable(StreamPipeOutputEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(streamOutput))
        {
            return NativeMuxTasks.GetOrAdd(streamOutput, _ => Task.Run(async () =>
            {
                Logger.InfoMarkUp($"[deepskyblue1]FFmpeg-free native MPEG-TS pipe output:[/] {streamOutput.EscapeMarkup()}");
                Logger.InfoMarkUp("[deepskyblue1]PIPE native mux task started.[/]");
                Logger.InfoMarkUp($"[deepskyblue1]PIPE native mux inputs: {pipeNames.Length}[/]");
                var result = await NativeFmp4TsMuxer.RunAsync(pipeNames, streamOutput);
                Logger.InfoMarkUp($"[deepskyblue1]Native fMP4 -> MPEG-TS muxer returned: {result}[/]");
                NativePipeRegistries.TryRemove(streamOutput, out _);
                return result;
            }));
        }

        return Task.Run(() =>
        {
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
            if (OperatingSystem.IsWindows()) command.Append($" -i \"\\\\.\\pipe\\{item}\" ");
            else command.Append($" -i \"{Path.Combine(pipeDir, item)}\" ");
        }
        for (var i = 0; i < pipeNames.Length; i++) command.Append($" -map {i} ");
        command.Append(" -strict unofficial -c copy ");
        command.Append($" -metadata date=\"{dateString}\" -ignore_unknown -copy_unknown ");
        if (!string.IsNullOrEmpty(customDest))
        {
            if (customDest.Trim().StartsWith('-')) command.Append(customDest);
            else command.Append($" -f mpegts -shortest \"{customDest}\"");
            Logger.WarnMarkUp($"[deepskyblue1]{command.ToString().EscapeMarkup()}[/]");
        }
        else command.Append($" -f mpegts -shortest \"{outputPath}\"");

        using var p = new Process { StartInfo = new ProcessStartInfo {
            WorkingDirectory = Environment.CurrentDirectory, FileName = binary, Arguments = command.ToString(),
            CreateNoWindow = true, UseShellExecute = false } };
        p.Start(); p.WaitForExit();
        return p.ExitCode == 0;
    }
}