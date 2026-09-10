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
        ThreadPool.GetAvailableThreads(out var availableWorkers, out var availableIo);
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        Logger.WarnMarkUp($"[PIPE-TRACE] ThreadPool entry workers={availableWorkers}/{maxWorkers} io={availableIo}/{maxIo} min={minWorkers}/{minIo}");
        Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: before OS check");

        if (OperatingSystem.IsWindows())
        {
            Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: OS check complete windows=True");
            Logger.InfoMarkUp($"[yellow]PIPE create (Windows): {pipeName.EscapeMarkup()}[/]");
            var stream = new NamedPipeServerStream(pipeName, PipeDirection.InOut);
            Logger.InfoMarkUp($"[yellow]PIPE Windows stream opened: {pipeName.EscapeMarkup()}[/]");
            return stream;
        }

        Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: OS check complete windows=False");
        Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: before Path.GetTempPath");
        var tempPath = Path.GetTempPath();
        Logger.WarnMarkUp($"[PIPE-TRACE] CreatePipe checkpoint: Path.GetTempPath complete path={tempPath.EscapeMarkup()}");
        Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: before Path.Combine");
        var path = Path.Combine(tempPath, pipeName);
        Logger.WarnMarkUp($"[PIPE-TRACE] CreatePipe checkpoint: Path.Combine complete path={path.EscapeMarkup()}");
        Logger.InfoMarkUp($"[yellow]PIPE create (FIFO) begin: {path.EscapeMarkup()}[/]");

        try
        {
            Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: before File.Exists");
            var exists = File.Exists(path);
            Logger.WarnMarkUp($"[PIPE-TRACE] CreatePipe checkpoint: File.Exists complete exists={exists}");
            if (exists)
            {
                Logger.WarnMarkUp($"[PIPE-TRACE] FIFO path already exists; removing stale path: {path.EscapeMarkup()}");
                Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: before File.Delete");
                File.Delete(path);
                Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: File.Delete complete");
            }

            Logger.InfoMarkUp($"[yellow]PIPE mkfifo begin: {path.EscapeMarkup()}[/]");
            Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: before mkfifo");
            var rc = mkfifo(path, 0x180u); // 0600
            var errno = Marshal.GetLastWin32Error();
            Logger.WarnMarkUp($"[PIPE-TRACE] CreatePipe checkpoint: mkfifo complete rc={rc} errno={errno}");
            Logger.InfoMarkUp($"[yellow]PIPE mkfifo returned rc={rc} errno={errno}[/]");
            if (rc != 0)
                throw new IOException($"mkfifo failed for '{path}' with errno {errno}");

            Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: before File.GetAttributes");
            var attributes = File.GetAttributes(path);
            Logger.WarnMarkUp($"[PIPE-TRACE] CreatePipe checkpoint: File.GetAttributes complete attributes={attributes}");
            Logger.InfoMarkUp($"[yellow]PIPE FIFO exists/type={attributes}; opening read/write[/]");

            // Open an existing FIFO read/write. This avoids waiting for a separate
            // reader/writer and keeps CreatePipe non-blocking while the native muxer
            // attaches to the FIFO asynchronously.
            Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: before FileStream open");
            var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: FileStream open complete");
            Logger.InfoMarkUp($"[yellow]PIPE opened: {path.EscapeMarkup()}[/]");

            Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: before native registration");
            RegisterNativePipeAndMaybeStartMux(pipeName);
            Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: native registration complete");

            // IMPORTANT: return immediately. The producer must never wait for the
            // native muxer here (or on its first write), because CreatePipe/CopyTo
            // runs in the parallel stream workers. Native mux startup is triggered
            // once both FIFOs are registered, and FIFO backpressure is allowed to
            // regulate the producers naturally.
            Logger.WarnMarkUp("[PIPE-TRACE] CreatePipe checkpoint: returning stream");
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
            var task = NativeMuxTasks.GetOrAdd(streamOutput, key =>
            {
                Logger.InfoMarkUp("[deepskyblue1]PIPE native mux task queueing on dedicated thread.[/]");
                return Task.Factory.StartNew(async () =>
                {
                    try
                    {
                        Logger.InfoMarkUp($"[deepskyblue1]FFmpeg-free native MPEG-TS pipe output:[/] {streamOutput.EscapeMarkup()}");
                        Logger.InfoMarkUp("[deepskyblue1]PIPE native mux task started.[/]");
                        Logger.InfoMarkUp($"[deepskyblue1]PIPE native mux inputs: {pipeNames.Length}[/]");

                        // The native muxer is a long-lived I/O pipeline. Run its initial
                        // FIFO attachment on a dedicated thread so it cannot be delayed
                        // behind producer/download work on the managed ThreadPool.
                        var result = await NativeFmp4TsMuxer.RunAsync(pipeNames, streamOutput).ConfigureAwait(false);
                        Logger.InfoMarkUp($"[deepskyblue1]Native fMP4 -> MPEG-TS muxer returned: {result}[/]");
                        return result;
                    }
                    catch (Exception ex)
                    {
                        Logger.ErrorMarkUp($"[red]PIPE native mux worker failed: {ex.GetType().Name}: {ex.Message.EscapeMarkup()}[/]");
                        return false;
                    }
                    finally
                    {
                        NativePipeRegistries.TryRemove(key, out ConcurrentDictionary<string, byte>? removedRegistry);
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            });
            return task;
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