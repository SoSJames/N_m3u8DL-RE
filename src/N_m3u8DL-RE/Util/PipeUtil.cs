using N_m3u8DL_RE.Common.Log;
using Spectre.Console;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
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
        // SimpleLiveRecordManager2 calls this method for the established live-pipe path.
        // This experimental branch intentionally replaces the FFmpeg process with the
        // embedded Shaka Packager process. There is no PATH lookup, environment-variable
        // lookup, or externally supplied Shaka executable.
        Logger.WarnMarkUp("[deepskyblue1][SHAKA-EMBEDDED] StartPipeMuxAsync reached[/]");
        return await StartEmbeddedShakaLiveAsync(pipeNames, outputPath);
    }

    private static async Task<bool> StartEmbeddedShakaLiveAsync(string[] pipeNames, string outputPath)
    {
        if (pipeNames.Length == 0 || pipeNames.Length > 2)
        {
            Logger.ErrorMarkUp("[SHAKA-EMBEDDED] Supports one video pipe or one video + one audio pipe.");
            return false;
        }

        string? shakaBinary = null;
        try
        {
            shakaBinary = ExtractEmbeddedShakaPackager();

            var outputDir = Path.Combine(
                Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory,
                Path.GetFileNameWithoutExtension(outputPath) + ".hls");
            Directory.CreateDirectory(outputDir);

            var videoDir = Path.Combine(outputDir, "video");
            Directory.CreateDirectory(videoDir);

            var audioDir = Path.Combine(outputDir, "audio");
            if (pipeNames.Length == 2)
                Directory.CreateDirectory(audioDir);

            var videoPipe = GetPipePath(pipeNames[0]);
            var videoDescriptor =
                $"in=\"{videoPipe}\",stream=video,init_segment=\"{Path.Combine(videoDir, "init.mp4")}\"," +
                $"segment_template=\"{Path.Combine(videoDir, "$Number$.m4s")}\",playlist_name=\"video.m3u8\"";

            var master = Path.Combine(outputDir, "master.m3u8");

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                WorkingDirectory = Environment.CurrentDirectory,
                FileName = shakaBinary,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            // ArgumentList deliberately avoids manual command-line quoting.
            process.StartInfo.ArgumentList.Add(videoDescriptor);

            if (pipeNames.Length == 2)
            {
                var audioPipe = GetPipePath(pipeNames[1]);
                var audioDescriptor =
                    $"in=\"{audioPipe}\",stream=audio,language=en,hls_name=English," +
                    $"init_segment=\"{Path.Combine(audioDir, "init.mp4")}\"," +
                    $"segment_template=\"{Path.Combine(audioDir, "$Number$.m4s")}\"," +
                    $"playlist_name=\"audio.m3u8\",hls_group_id=audio";
                process.StartInfo.ArgumentList.Add(audioDescriptor);
            }

            process.StartInfo.ArgumentList.Add("--hls_master_playlist_output");
            process.StartInfo.ArgumentList.Add(master);
            process.StartInfo.ArgumentList.Add("--hls_playlist_type");
            process.StartInfo.ArgumentList.Add("LIVE");
            process.StartInfo.ArgumentList.Add("--segment_duration");
            process.StartInfo.ArgumentList.Add("2.002");
            process.StartInfo.ArgumentList.Add("--fragment_duration");
            process.StartInfo.ArgumentList.Add("2.002");
            process.StartInfo.ArgumentList.Add("--time_shift_buffer_depth");
            process.StartInfo.ArgumentList.Add("24");
            process.StartInfo.ArgumentList.Add("--preserved_segments_outside_live_window");
            process.StartInfo.ArgumentList.Add("24");
            process.StartInfo.ArgumentList.Add("--suggested_presentation_delay");
            process.StartInfo.ArgumentList.Add("8");
            process.StartInfo.ArgumentList.Add("--default_language");
            process.StartInfo.ArgumentList.Add("en");
            process.StartInfo.ArgumentList.Add("--io_block_size");
            process.StartInfo.ArgumentList.Add("65536");

            Logger.WarnMarkUp("[deepskyblue1][SHAKA-EMBEDDED] Launching embedded Shaka Packager[/]");
            Logger.InfoMarkUp($"[deepskyblue1][SHAKA-EMBEDDED] Binary: {shakaBinary.EscapeMarkup()}[/]");
            Logger.InfoMarkUp($"[deepskyblue1][SHAKA-EMBEDDED] Pipes: {string.Join(", ", pipeNames).EscapeMarkup()}[/]");
            Logger.InfoMarkUp($"HLS output: [cyan]{master.EscapeMarkup()}[/]");

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
            Logger.WarnMarkUp($"[SHAKA-EMBEDDED] Shaka Packager exited with code {process.ExitCode}");
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Logger.ErrorMarkUp($"[SHAKA-EMBEDDED] Failed to launch embedded Shaka Packager: {ex.Message.EscapeMarkup()}");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(shakaBinary))
            {
                try
                {
                    File.Delete(shakaBinary);
                }
                catch
                {
                    // Best effort cleanup only.
                }
            }
        }
    }

    private static string ExtractEmbeddedShakaPackager()
    {
        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var suffix = $".Assets.Shaka.{rid}.packager{extension}";

        var resourceName = Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .SingleOrDefault(x => x.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            throw new FileNotFoundException($"Embedded Shaka Packager resource not found for {rid}.");

        var root = Path.Combine(Path.GetTempPath(), "N_m3u8DL-RE", "shaka");
        Directory.CreateDirectory(root);

        var path = Path.Combine(root, $"{rid}-{Guid.NewGuid():N}{extension}");
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Unable to open embedded Shaka resource '{resourceName}'.");

        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush();

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Logger.InfoMarkUp($"[SHAKA-EMBEDDED] Extracted resource: {resourceName.EscapeMarkup()}");
        return path;
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