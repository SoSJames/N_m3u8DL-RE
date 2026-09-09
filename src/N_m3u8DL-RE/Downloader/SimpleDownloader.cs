using N_m3u8DL_RE.Common.Entity;
using N_m3u8DL_RE.Common.Enum;
using N_m3u8DL_RE.Common.Log;
using N_m3u8DL_RE.Config;
using N_m3u8DL_RE.Crypto;
using N_m3u8DL_RE.Entity;
using N_m3u8DL_RE.Util;
using Spectre.Console;

namespace N_m3u8DL_RE.Downloader;

/// <summary>
/// 简单下载器
/// </summary>
internal class SimpleDownloader : IDownloader
{
    DownloaderConfig DownloaderConfig;

    public SimpleDownloader(DownloaderConfig config)
    {
        DownloaderConfig = config;
    }

    public async Task<DownloadResult?> DownloadSegmentAsync(MediaSegment segment, string savePath, SpeedContainer speedContainer, Dictionary<string, string>? headers = null)
    {
        Logger.WarnMarkUp($"[PIPE-TRACE] DOWNLOAD START idx={segment.Index} path={Path.GetFileName(savePath)}");
        var url = segment.Url;
        var (des, dResult) = await DownClipAsync(url, savePath, speedContainer, segment.StartRange, segment.StopRange, headers, DownloaderConfig.MyOptions.DownloadRetryCount);
        if (dResult is { Success: true } && dResult.ActualFilePath != des)
        {
            switch (segment.EncryptInfo.Method)
            {
                case EncryptMethod.AES_128:
                {
                    var key = segment.EncryptInfo.Key;
                    var iv = segment.EncryptInfo.IV;
                    AESUtil.AES128Decrypt(dResult.ActualFilePath, key!, iv!);
                    break;
                }
                case EncryptMethod.AES_128_ECB:
                {
                    var key = segment.EncryptInfo.Key;
                    var iv = segment.EncryptInfo.IV;
                    AESUtil.AES128Decrypt(dResult.ActualFilePath, key!, iv!, System.Security.Cryptography.CipherMode.ECB);
                    break;
                }
                case EncryptMethod.CHACHA20:
                {
                    var key = segment.EncryptInfo.Key;
                    var nonce = segment.EncryptInfo.IV;
                    var fileBytes = File.ReadAllBytes(dResult.ActualFilePath);
                    var decrypted = ChaCha20Util.DecryptPer1024Bytes(fileBytes, key!, nonce!);
                    await File.WriteAllBytesAsync(dResult.ActualFilePath, decrypted);
                    break;
                }
                case EncryptMethod.SAMPLE_AES_CTR:
                    break;
            }

            if (dResult.ImageHeader)
            {
                await ImageHeaderUtil.ProcessAsync(dResult.ActualFilePath);
            }
            if (dResult.GzipHeader)
            {
                await OtherUtil.DeGzipFileAsync(dResult.ActualFilePath);
            }

            File.Move(dResult.ActualFilePath, des);
            dResult.ActualFilePath = des;
        }
        if (dResult is { Success: true })
        {
            long size = 0;
            try { size = new FileInfo(dResult.ActualFilePath).Length; } catch { }
            Logger.WarnMarkUp($"[PIPE-TRACE] DOWNLOAD DONE idx={segment.Index} bytes={size} path={Path.GetFileName(dResult.ActualFilePath)}");
        }
        else
        {
            Logger.WarnMarkUp($"[PIPE-TRACE] DOWNLOAD FAILED idx={segment.Index}");
        }
        return dResult;
    }

    private async Task<(string des, DownloadResult? dResult)> DownClipAsync(string url, string path, SpeedContainer speedContainer, long? fromPosition, long? toPosition, Dictionary<string, string>? headers = null, int retryCount = 3)
    {
        CancellationTokenSource? cancellationTokenSource = null;
        retry:
        try
        {
            cancellationTokenSource = new();
            var des = Path.ChangeExtension(path, null);

            if (File.Exists(des))
            {
                speedContainer.Add(new FileInfo(des).Length);
                return (des, new DownloadResult() { ActualContentLength = 0, ActualFilePath = des });
            }

            var dec = Path.Combine(Path.GetDirectoryName(des)!, Path.GetFileNameWithoutExtension(des) + "_dec" + Path.GetExtension(des));
            if (File.Exists(dec))
            {
                speedContainer.Add(new FileInfo(dec).Length);
                return (dec, new DownloadResult() { ActualContentLength = 0, ActualFilePath = dec });
            }

            var cts = cancellationTokenSource;
            using var watcher = Task.Factory.StartNew(async () =>
            {
                while (true)
                {
                    if (cts.IsCancellationRequested) break;
                    if (speedContainer.ShouldStop)
                    {
                        cts.Cancel();
                        Logger.DebugMarkUp("Cancel...");
                        break;
                    }
                    await Task.Delay(500);
                }
            });

            var result = await DownloadUtil.DownloadToFileAsync(url, path, speedContainer, cancellationTokenSource, headers, fromPosition, toPosition);
            return (des, result);
        }
        catch (Exception ex)
        {
            Logger.DebugMarkUp($"[grey]{ex.Message.EscapeMarkup()} retryCount: {retryCount}[/]");
            Logger.Debug(url + " " + ex);
            Logger.Extra($"Ah oh!{Environment.NewLine}RetryCount => {retryCount}{Environment.NewLine}Exception  => {ex.Message}{Environment.NewLine}Url        => {url}");
            if (retryCount-- > 0)
            {
                await Task.Delay(1000);
                goto retry;
            }
            else
            {
                Logger.Extra($"The retry attempts have been exhausted and the download of this segment has failed.{Environment.NewLine}Exception  => {ex.Message}{Environment.NewLine}Url        => {url}");
                Logger.WarnMarkUp($"[grey]{ex.Message.EscapeMarkup()}[/]");
            }
            return default;
        }
        finally
        {
            if (cancellationTokenSource != null)
            {
                cancellationTokenSource.Dispose();
                cancellationTokenSource = null;
            }
        }
    }
}