from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANAGER = ROOT / "src/N_m3u8DL-RE/DownloadManager/SimpleLiveRecordManager2.cs"
DASH = ROOT / "src/N_m3u8DL-RE.Parser/Extractor/DASHExtractor2.cs"


def replace_once(text, old, new, label):
    if old not in text:
        raise RuntimeError(f"Patch target not found: {label}")
    return text.replace(old, new, 1)


# DASH: when overlapping Periods contain the same logical track, retain the newest Period.
dash = DASH.read_text(encoding="utf-8-sig")
dash = replace_once(
    dash,
    '                    var _index = streamList.FindIndex(_f => _f.PeriodId != streamSpec.PeriodId && _f.GroupId == streamSpec.GroupId && _f.Resolution == streamSpec.Resolution && _f.MediaType == streamSpec.MediaType);',
    '''                    var _index = streamList.FindIndex(_f =>
                        _f.PeriodId != streamSpec.PeriodId &&
                        _f.MediaType == streamSpec.MediaType &&
                        _f.Resolution == streamSpec.Resolution &&
                        (_f.GroupId == streamSpec.GroupId || (isLive && IsSameLogicalLiveTrack(_f, streamSpec))));''',
    "DASH logical Period matching",
)
dash = replace_once(
    dash,
    '''                        if (isLive)
                        {
                            // 直播，这种情况直接略过新的
                        }''',
    '''                        if (isLive)
                        {
                            // Overlapping live Periods are common at ad/content boundaries.
                            // Keep the newest Period so auto-selection follows the active timeline.
                            Logger.Debug($"[DASH] Prefer newest live Period for {streamSpec.GroupId} ({streamSpec.MediaType}, {streamSpec.Resolution})");
                            streamList[_index] = streamSpec;
                        }''',
    "DASH newest Period replacement",
)
DASH.write_text(dash, encoding="utf-8")


# Live recorder resilience.
text = MANAGER.read_text(encoding="utf-8-sig")

text = replace_once(
    text,
    '''    ConcurrentDictionary<int, long> DateTimeDic = new(); // 上次下载的dateTime
    CancellationTokenSource CancellationTokenSource = new(); // 取消Wait
    List<Regex> AdKeywordRegexList = []; // 广告关键字正则（直播刷新时复用）''',
    '''    ConcurrentDictionary<int, long> DateTimeDic = new(); // 上次下载的dateTime
    ConcurrentDictionary<int, string> PeriodIdDic = new(); // 当前直播Period
    ConcurrentDictionary<int, int> RecoveryGenerationDic = new(); // 每流恢复代数
    ConcurrentDictionary<int, DateTime> LastRefreshUtcDic = new(); // 最近一次成功刷新MPD
    ConcurrentDictionary<int, DateTime> LastMediaProgressUtcDic = new(); // 最近一次成功媒体输出
    ConcurrentDictionary<int, bool> HasMediaProgressDic = new(); // 是否曾经成功输出过媒体
    ConcurrentDictionary<int, int> RefreshFailureDic = new(); // 连续刷新失败次数
    CancellationTokenSource CancellationTokenSource = new(); // 取消Wait
    List<Regex> AdKeywordRegexList = []; // 广告关键字正则（直播刷新时复用）

    // Keep recovery latency short for live playback. Do not use long exponential backoff:
    // a live stream can lose its playback buffer after only a few seconds.
    private const int LiveDownloadAttempts = 3;
    private const int LiveRetryDelayMs = 1000;
    private const int LiveStallSeconds = 8;''',
    "resilience state",
)

helpers = '''
    private void MarkMediaProgress(int taskId)
    {
        LastMediaProgressUtcDic[taskId] = DateTime.UtcNow;
        HasMediaProgressDic[taskId] = true;
    }

    private void ResetLiveTrackState(int taskId, string reason)
    {
        LastFileNameDic[taskId] = "";
        DateTimeDic[taskId] = 0L;
        MaxIndexDic[taskId] = 0L;
        SamePathDic.TryRemove(taskId, out _);
        RecoveryGenerationDic.AddOrUpdate(taskId, 1, (_, value) => value + 1);
        Logger.WarnMarkUp($"[LIVE] Resetting track state for task {taskId}: {reason}");
    }

    private async Task<DownloadResult?> DownloadSegmentWithRetryAsync(
        MediaSegment segment,
        string path,
        SpeedContainer speedContainer,
        Dictionary<string, string> headers,
        int taskId,
        string kind)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= LiveDownloadAttempts; attempt++)
        {
            try
            {
                var result = await Downloader.DownloadSegmentAsync(segment, path, speedContainer, headers);
                if (result is { Success: true })
                    return result;

                Logger.WarnMarkUp($"[LIVE] {kind} download attempt {attempt}/{LiveDownloadAttempts} failed: {GetSegmentName(segment, false, false)}");
            }
            catch (Exception ex)
            {
                lastException = ex;
                Logger.WarnMarkUp($"[LIVE] {kind} download attempt {attempt}/{LiveDownloadAttempts} threw: {ex.Message}");
            }

            if (attempt < LiveDownloadAttempts)
                await Task.Delay(LiveRetryDelayMs);
        }

        if (lastException != null)
            Logger.WarnMarkUp($"[LIVE] {kind} download exhausted retries: {lastException.Message}");
        return null;
    }

    private async Task<bool> DecryptWithRetryAsync(
        DecryptEngine decryptEngine,
        string decryptionBinaryPath,
        MediaSegment segment,
        string encryptedPath,
        string decryptedPath,
        string currentKID,
        string? mp4InitFile = null)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var result = await MP4DecryptUtil.DecryptAsync(
                    decryptEngine,
                    decryptionBinaryPath,
                    DownloaderConfig.MyOptions.Keys,
                    encryptedPath,
                    decryptedPath,
                    currentKID,
                    mp4InitFile);
                if (result)
                    return true;
                Logger.WarnMarkUp($"[LIVE] Decryption attempt {attempt}/2 failed: {GetSegmentName(segment, false, false)}");
            }
            catch (Exception ex)
            {
                Logger.WarnMarkUp($"[LIVE] Decryption attempt {attempt}/2 threw: {ex.Message}");
            }

            if (attempt < 2)
                await Task.Delay(250);
        }

        return false;
    }

    private async Task MonitorLiveHealthAsync(Dictionary<StreamSpec, ProgressTask> dic)
    {
        while (!STOP_FLAG)
        {
            await Task.Delay(1000);
            if (STOP_FLAG) break;

            var now = DateTime.UtcNow;
            foreach (var pair in dic)
            {
                var taskId = pair.Value.Id;
                if (RecordLimitReachedDic[taskId] || LiveEndDic[taskId])
                    continue;
                if (!HasMediaProgressDic.TryGetValue(taskId, out var hasProgress) || !hasProgress)
                    continue;
                if (!LastRefreshUtcDic.TryGetValue(taskId, out var lastRefresh) ||
                    !LastMediaProgressUtcDic.TryGetValue(taskId, out var lastProgress))
                    continue;

                var refreshAge = (now - lastRefresh).TotalSeconds;
                var progressAge = (now - lastProgress).TotalSeconds;
                if (refreshAge >= LiveStallSeconds && progressAge >= LiveStallSeconds)
                {
                    Logger.WarnMarkUp($"[LIVE] {pair.Key.MediaType} stalled for {LiveStallSeconds}s; rebuilding live state");
                    ResetLiveTrackState(taskId, "media output stalled while MPD refreshes continued");
                    LastRefreshUtcDic[taskId] = now;
                }
            }
        }
    }

'''
text = replace_once(
    text,
    '    private async Task<bool> RecordStreamAsync(StreamSpec streamSpec, ProgressTask task, SpeedContainer speedContainer, BufferBlock<List<MediaSegment>> source)\n    {',
    helpers + '''    private async Task<bool> RecordStreamAsync(StreamSpec streamSpec, ProgressTask task, SpeedContainer speedContainer, BufferBlock<List<MediaSegment>> source)
    {
        while (!STOP_FLAG)
        {
            try
            {
                return await RecordStreamCoreAsync(streamSpec, task, speedContainer, source);
            }
            catch (OperationCanceledException) when (STOP_FLAG)
            {
                return true;
            }
            catch (Exception ex)
            {
                Logger.ErrorMarkUp($"[LIVE] Recorder task {task.Id} recovered from an unexpected exception: {ex}");
                ResetLiveTrackState(task.Id, "unexpected recorder exception");
                await Task.Delay(LiveRetryDelayMs);
            }
        }
        return true;
    }

    private async Task<bool> RecordStreamCoreAsync(StreamSpec streamSpec, ProgressTask task, SpeedContainer speedContainer, BufferBlock<List<MediaSegment>> source)
    {''',
    "recorder recovery wrapper",
)

text = replace_once(
    text,
    '''        bool firstSub = true;
        task.StartTask();''',
    '''        bool firstSub = true;
        var activePeriodId = streamSpec.PeriodId;
        var localRecoveryGeneration = RecoveryGenerationDic.TryGetValue(task.Id, out var initialGeneration) ? initialGeneration : 0;
        task.StartTask();''',
    "consumer local recovery state",
)

text = replace_once(
    text,
    '''            var segments = segmentsList!.SelectMany(s => s);
            if (segments == null || !segments.Any()) continue;
            var segmentsDuration = segments.Sum(s => s.Duration);''',
    '''            var segments = segmentsList!.SelectMany(s => s);
            if (segments == null || !segments.Any()) continue;

            var currentGeneration = RecoveryGenerationDic.TryGetValue(task.Id, out var generation) ? generation : 0;
            if (!string.Equals(activePeriodId, streamSpec.PeriodId, StringComparison.Ordinal) || currentGeneration != localRecoveryGeneration)
            {
                Logger.WarnMarkUp($"[LIVE] Resetting consumer state for {streamSpec.MediaType}: Period {activePeriodId ?? "<none>"} -> {streamSpec.PeriodId ?? "<none>"}, generation {localRecoveryGeneration} -> {currentGeneration}");
                activePeriodId = streamSpec.PeriodId;
                localRecoveryGeneration = currentGeneration;
                initDownloaded = false;
                currentKID = "";
                mp4InitFile = "";
                readInfo = false;
                mediaInfos = [];
                FileDic.Clear();
                currentVtt = new WebVttSub();
                firstSub = true;
            }

            var segmentsDuration = segments.Sum(s => s.Duration);''',
    "consumer Period/generation reset",
)

# Replace all direct init/segment downloads with retrying helper.
text = text.replace(
    'var result = await Downloader.DownloadSegmentAsync(streamSpec.Playlist.MediaInit, path, speedContainer, headers);',
    'var result = await DownloadSegmentWithRetryAsync(streamSpec.Playlist.MediaInit, path, speedContainer, headers, task.Id, "init");',
)
text = text.replace(
    'var result = await Downloader.DownloadSegmentAsync(seg, path, speedContainer, headers);',
    'var result = await DownloadSegmentWithRetryAsync(seg, path, speedContainer, headers, task.Id, "segment");',
)

# Make failed init/first-segment attempts recoverable rather than fatal.
text = replace_once(
    text,
    '''                if (result is not { Success: true })
                {
                    throw new Exception("Download init file failed!");
                }''',
    '''                if (result is not { Success: true })
                {
                    ResetLiveTrackState(task.Id, "initialization download failed after retries");
                    continue;
                }''',
    "recoverable init failure",
)
text = replace_once(
    text,
    '''                if (result is not { Success: true })
                {
                    throw new Exception("Download first segment failed!");
                }''',
    '''                if (result is not { Success: true })
                {
                    ResetLiveTrackState(task.Id, "first segment download failed after retries");
                    continue;
                }''',
    "recoverable first segment failure",
)

# Replace direct decryption calls with bounded retries.
text = text.replace('var dResult = await MP4DecryptUtil.DecryptAsync(', 'var dResult = await DecryptWithRetryAsync(')
# The helper signature is intentionally compatible with the existing call shape, but the first
# argument now needs the segment for diagnostics. Fix the two segment call sites and init call.
text = text.replace(
    'var dResult = await DecryptWithRetryAsync(decryptEngine, decryptionBinaryPath, DownloaderConfig.MyOptions.Keys, enc, dec, currentKID);',
    'var dResult = await DecryptWithRetryAsync(decryptEngine, decryptionBinaryPath, streamSpec.Playlist.MediaInit!, enc, dec, currentKID);',
)
text = text.replace(
    'var dResult = await DecryptWithRetryAsync(decryptEngine, decryptionBinaryPath, DownloaderConfig.MyOptions.Keys, enc, dec, currentKID, mp4InitFile);',
    'var dResult = await DecryptWithRetryAsync(decryptEngine, decryptionBinaryPath, seg, enc, dec, currentKID, mp4InitFile);',
)

# Parallel segment downloads: failed segments are skipped instead of poisoning FileDic.
text = replace_once(
    text,
    '''                var result = await DownloadSegmentWithRetryAsync(seg, path, speedContainer, headers);
                FileDic[seg] = result;
                if (result is { Success: true })
                    task.Increment(1);
                // 实时解密
                if (seg.IsEncrypted && DownloaderConfig.MyOptions.MP4RealTimeDecryption && result is { Success: true } && !string.IsNullOrEmpty(currentKID))
                {
                    var enc = result.ActualFilePath;
                    var dec = Path.Combine(Path.GetDirectoryName(enc)!, Path.GetFileNameWithoutExtension(enc) + "_dec" + Path.GetExtension(enc));
                    var dResult = await DecryptWithRetryAsync(decryptEngine, decryptionBinaryPath, seg, enc, dec, currentKID, mp4InitFile);
                    if (dResult)
                    {
                        File.Delete(enc);
                        result.ActualFilePath = dec;
                    }
                }''',
    '''                var result = await DownloadSegmentWithRetryAsync(seg, path, speedContainer, headers, task.Id, "segment");
                if (result is not { Success: true })
                    return;
                FileDic[seg] = result;
                task.Increment(1);
                // 实时解密
                if (seg.IsEncrypted && DownloaderConfig.MyOptions.MP4RealTimeDecryption && !string.IsNullOrEmpty(currentKID))
                {
                    var enc = result.ActualFilePath;
                    var dec = Path.Combine(Path.GetDirectoryName(enc)!, Path.GetFileNameWithoutExtension(enc) + "_dec" + Path.GetExtension(enc));
                    var dResult = await DecryptWithRetryAsync(decryptEngine, decryptionBinaryPath, seg, enc, dec, currentKID, mp4InitFile);
                    if (dResult)
                    {
                        File.Delete(enc);
                        result.ActualFilePath = dec;
                        MarkMediaProgress(task.Id);
                    }
                }
                else
                {
                    MarkMediaProgress(task.Id);
                }''',
    "parallel resilient download block",
)

# First segment progress marker.
text = replace_once(
    text,
    '''                        if (dResult)
                        {
                            File.Delete(enc);
                            result.ActualFilePath = dec;
                        }
                    }
                    if (!readInfo)''',
    '''                        if (dResult)
                        {
                            File.Delete(enc);
                            result.ActualFilePath = dec;
                            MarkMediaProgress(task.Id);
                        }
                    }
                    else
                    {
                        MarkMediaProgress(task.Id);
                    }
                    if (!readInfo)''',
    "first segment progress marker",
)

# Init decryption call has no segment variable; mark no media progress there.

# Producer: detect Period changes before applying old deduplication state.
text = replace_once(
    text,
    '''                var allHasDatetime = streamSpec.Playlist!.MediaParts[0].MediaSegments.All(s => s.DateTime != null);''',
    '''                var currentPeriodId = streamSpec.PeriodId ?? "";
                var previousPeriodId = PeriodIdDic[task.Id];
                if (!string.Equals(previousPeriodId, currentPeriodId, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(previousPeriodId))
                        Logger.WarnMarkUp($"[LIVE] DASH Period changed for {streamSpec.MediaType}: {previousPeriodId} -> {currentPeriodId}");
                    PeriodIdDic[task.Id] = currentPeriodId;
                    ResetLiveTrackState(task.Id, "DASH Period transition");
                    PeriodIdDic[task.Id] = currentPeriodId;
                }

                var allHasDatetime = streamSpec.Playlist!.MediaParts[0].MediaSegments.All(s => s.DateTime != null);''',
    "producer Period transition",
)

# Producer refresh: retry quickly and never terminate the live producer on transient refresh failure.
text = replace_once(
    text,
    '''            try
            {
                // Logger.WarnMarkUp($"wait {waitSec}s");
                if (!STOP_FLAG) await Task.Delay(WAIT_SEC * 1000, CancellationTokenSource.Token);
                // 刷新列表
                if (!STOP_FLAG) await StreamExtractor.RefreshPlayListAsync(dic.Keys.ToList());
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == CancellationTokenSource.Token)
            {
                // 不需要做事
            }
            catch (Exception e)
            {
                Logger.ErrorMarkUp(e);
                STOP_FLAG = true;
                // 停止所有Block
                foreach (var target in BlockDic.Values)
                {
                    target.Complete();
                }
            }''',
    '''            try
            {
                // Keep the live retry interval short. A long exponential backoff would
                // consume the player's buffer and turn a transient outage into a freeze.
                if (!STOP_FLAG) await Task.Delay(WAIT_SEC * 1000, CancellationTokenSource.Token);
                if (STOP_FLAG) continue;

                var refreshed = false;
                Exception? lastRefreshException = null;
                for (var attempt = 1; attempt <= 3 && !STOP_FLAG; attempt++)
                {
                    try
                    {
                        await StreamExtractor.RefreshPlayListAsync(dic.Keys.ToList());
                        refreshed = true;
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        lastRefreshException = ex;
                        RefreshFailureDic.AddOrUpdate(-1, 1, (_, value) => value + 1);
                        Logger.WarnMarkUp($"[LIVE] MPD refresh attempt {attempt}/3 failed: {ex.Message}");
                        if (attempt < 3)
                            await Task.Delay(LiveRetryDelayMs, CancellationTokenSource.Token);
                    }
                }

                if (refreshed)
                {
                    var now = DateTime.UtcNow;
                    foreach (var pair in dic)
                    {
                        LastRefreshUtcDic[pair.Value.Id] = now;
                        RefreshFailureDic[pair.Value.Id] = 0;
                    }
                }
                else
                {
                    Logger.WarnMarkUp($"[LIVE] MPD refresh unavailable after 3 quick attempts; keeping recorder alive and rebuilding track state.");
                    foreach (var pair in dic)
                        ResetLiveTrackState(pair.Value.Id, "MPD refresh temporarily unavailable");
                }
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == CancellationTokenSource.Token)
            {
                // Normal stop.
            }''',
    "refresh retry handling",
)

# Initialize resilience state for each task and start health monitor.
text = replace_once(
    text,
    '''                LastFileNameDic[task.Id] = "";
                RecordLimitReachedDic[task.Id] = false;''',
    '''                LastFileNameDic[task.Id] = "";
                PeriodIdDic[task.Id] = item.PeriodId ?? "";
                RecoveryGenerationDic[task.Id] = 0;
                LastRefreshUtcDic[task.Id] = DateTime.UtcNow;
                LastMediaProgressUtcDic[task.Id] = DateTime.UtcNow;
                HasMediaProgressDic[task.Id] = false;
                RefreshFailureDic[task.Id] = 0;
                RecordLimitReachedDic[task.Id] = false;''',
    "resilience initialization",
)
text = replace_once(
    text,
    '''            // 开始刷新
            var producerTask = PlayListProduceAsync(dic);
            await Task.Delay(200);''',
    '''            // 开始刷新
            var producerTask = PlayListProduceAsync(dic);
            var healthTask = MonitorLiveHealthAsync(dic);
            await Task.Delay(200);''',
    "health monitor startup",
)
text = replace_once(
    text,
    '''            await Parallel.ForEachAsync(dic, options, async (kp, _) =>
            {
                var task = kp.Value;
                var consumerTask = RecordStreamAsync(kp.Key, task, SpeedContainerDic[task.Id], BlockDic[task.Id]);
                Results[kp.Key] = await consumerTask;
            });''',
    '''            await Parallel.ForEachAsync(dic, options, async (kp, _) =>
            {
                var task = kp.Value;
                var consumerTask = RecordStreamAsync(kp.Key, task, SpeedContainerDic[task.Id], BlockDic[task.Id]);
                Results[kp.Key] = await consumerTask;
            });

            STOP_FLAG = true;
            CancellationTokenSource.Cancel();
            try { await healthTask; } catch (OperationCanceledException) { }
            try { await producerTask; } catch (OperationCanceledException) { }''',
    "await background live tasks",
)

MANAGER.write_text(text, encoding="utf-8")
print("Live resilience patch applied.")
