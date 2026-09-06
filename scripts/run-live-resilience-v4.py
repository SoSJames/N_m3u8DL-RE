from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DASH = ROOT / "src/N_m3u8DL-RE.Parser/Extractor/DASHExtractor2.cs"
MANAGER = ROOT / "src/N_m3u8DL-RE/DownloadManager/SimpleLiveRecordManager2.cs"

# v4 is deliberately diagnostic-first. Keep the current live handoff behavior
# (manifest-order Period selection) and add enough tracing to identify exactly
# which Period/init/KID is selected at an ad/content boundary.
dash = DASH.read_text(encoding="utf-8-sig")

old_periods = '        var periods = mpdElement.Elements().Where(e => e.Name.LocalName == "Period");'
new_periods = '''        var periods = mpdElement.Elements().Where(e => e.Name.LocalName == "Period");
        if (isLive)
        {
            var periodDiagnostics = periods.Select(p =>
            {
                var id = p.Attribute("id")?.Value ?? "";
                var start = p.Attribute("start")?.Value ?? "";
                var duration = p.Attribute("duration")?.Value ?? "";
                return $"{id}|start={start}|duration={duration}";
            });
            Logger.Debug($"[DASH][LIVE-MPD] Periods={string.Join(" || ", periodDiagnostics)}");
        }'''
if "[DASH][LIVE-MPD]" not in dash:
    if old_periods not in dash:
        raise RuntimeError("DASH Period enumeration anchor not found")
    dash = dash.replace(old_periods, new_periods, 1)

old_refresh = '        var newStreams = await ExtractStreamsAsync(rawText);\n        foreach (var streamSpec in streamSpecs)'
new_refresh = '''        var newStreams = await ExtractStreamsAsync(rawText);
        if (streamSpecs.Any(s => s.Playlist?.IsLive == true))
        {
            var candidates = newStreams.Select(n =>
                $"{n.MediaType}/{n.Resolution}/{n.GroupId}|Period={n.PeriodId}|Init={n.Playlist?.MediaInit?.Url ?? ""}|Parts={n.Playlist?.MediaParts.FirstOrDefault()?.MediaSegments.Count ?? 0}");
            Logger.Debug($"[DASH][LIVE-SELECT] Candidates={string.Join(" || ", candidates)}");
        }
        foreach (var streamSpec in streamSpecs)'''
if "[DASH][LIVE-SELECT] Candidates=" not in dash:
    if old_refresh not in dash:
        raise RuntimeError("DASH RefreshPlayList anchor not found")
    dash = dash.replace(old_refresh, new_refresh, 1)

old_match = '''            if (match.Any())
            {
                var matched = match.Last();
                streamSpec.PeriodId = matched.PeriodId;'''
new_match = '''            if (match.Any())
            {
                var candidates = match.ToList();
                // newStreams is built by iterating Periods in manifest order.
                // Keep the final matching entry so an ad/content handoff follows
                // the manifest's active Period ordering rather than lexical ID order.
                var matched = candidates.Last();
                if (streamSpec.Playlist?.IsLive == true)
                {
                    Logger.Debug($"[DASH][LIVE-SELECT] Track={streamSpec.MediaType}/{streamSpec.Resolution}/{streamSpec.GroupId} Matches={candidates.Count} SelectedPeriod={matched.PeriodId} SelectedInit={matched.Playlist?.MediaInit?.Url ?? ""} SelectedParts={matched.Playlist?.MediaParts.FirstOrDefault()?.MediaSegments.Count ?? 0}");
                }
                streamSpec.PeriodId = matched.PeriodId;'''
if "SelectedPeriod={matched.PeriodId}" not in dash:
    if old_match not in dash:
        raise RuntimeError("DASH live match anchor not found")
    dash = dash.replace(old_match, new_match, 1)

DASH.write_text(dash, encoding="utf-8")

manager = MANAGER.read_text(encoding="utf-8-sig")

old_state = '''        bool initDownloaded = false; // 是否下载过init文件
        ConcurrentDictionary<MediaSegment, DownloadResult?> FileDic = new();'''
new_state = '''        bool initDownloaded = false; // 是否下载过init文件
        string observedPeriodId = "";
        string observedInitUrl = "";
        string observedMpdKid = "";
        string initContextKey = "";
        ConcurrentDictionary<MediaSegment, DownloadResult?> FileDic = new();'''
if 'string initContextKey = "";' not in manager:
    if old_state not in manager:
        raise RuntimeError("SimpleLiveRecordManager2.cs state anchor not found")
    manager = manager.replace(old_state, new_state, 1)

old_batch = '''            var segmentsDuration = segments.Sum(s => s.Duration);
            Logger.DebugMarkUp(string.Join(",", segments.Select(sss => GetSegmentName(sss, false, false))));

            // 下载init'''
new_batch = '''            var segmentsDuration = segments.Sum(s => s.Duration);

            var incomingPeriodId = streamSpec.PeriodId ?? "";
            var incomingInitUrl = streamSpec.Playlist?.MediaInit?.Url ?? "";
            var incomingMpdKid = streamSpec.Playlist?.MediaInit?.EncryptInfo.KID ?? "";
            var initUrlChanged = incomingInitUrl != observedInitUrl;
            var mpdKidChanged = incomingMpdKid != observedMpdKid;
            var periodChanged = !string.IsNullOrEmpty(observedPeriodId) && incomingPeriodId != observedPeriodId;
            var segmentList = segments.ToList();
            var firstIndex = segmentList.Count == 0 ? -1 : segmentList.Min(s => s.Index);
            var lastIndex = segmentList.Count == 0 ? -1 : segmentList.Max(s => s.Index);
            Logger.Debug($"[LIVE][BATCH] {streamSpec.MediaType} Group={streamSpec.GroupId} Period={incomingPeriodId} Init={incomingInitUrl} KID={incomingMpdKid} Parts={segmentList.Count} Index={firstIndex}..{lastIndex}");
            if (periodChanged)
            {
                Logger.WarnMarkUp($"[LIVE][PERIOD] {streamSpec.MediaType} Group={streamSpec.GroupId} {observedPeriodId} -> {incomingPeriodId}; InitChanged={initUrlChanged}; KIDChanged={mpdKidChanged}");
            }
            // Period identity and initialization identity are separate. A Period
            // change that reuses the same init URL/KID must not reset decryption.
            if (initUrlChanged || mpdKidChanged)
            {
                initDownloaded = false;
                mp4InitFile = "";
                currentKID = "";
                initContextKey = "";
                Logger.Debug($"[LIVE][INIT-CONTEXT] New context required: Init={incomingInitUrl} KID={incomingMpdKid}");
            }
            observedPeriodId = incomingPeriodId;
            observedInitUrl = incomingInitUrl;
            observedMpdKid = incomingMpdKid;

            Logger.DebugMarkUp(string.Join(",", segmentList.Select(sss => GetSegmentName(sss, false, false))));

            // 下载init'''
if "[LIVE][BATCH]" not in manager:
    if old_batch not in manager:
        raise RuntimeError("SimpleLiveRecordManager2.cs batch anchor not found")
    manager = manager.replace(old_batch, new_batch, 1)

old_path = '                var path = Path.Combine(tmpDir, "_init.mp4.tmp");'
new_path = '''                if (string.IsNullOrEmpty(initContextKey))
                {
                    var contextBytes = System.Text.Encoding.UTF8.GetBytes($"{incomingInitUrl}\\n{incomingMpdKid}");
                    initContextKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contextBytes)).ToLowerInvariant()[..16];
                }
                var initDir = Path.Combine(tmpDir, $"init-context-{initContextKey}");
                Directory.CreateDirectory(initDir);
                Logger.Debug($"[LIVE][INIT-CONTEXT] Using {initDir}");
                var path = Path.Combine(initDir, "_init.mp4.tmp");'''
if 'Logger.Debug($"[LIVE][INIT-CONTEXT] Using {initDir}");' not in manager:
    if old_path not in manager:
        raise RuntimeError("SimpleLiveRecordManager2.cs init path anchor not found")
    manager = manager.replace(old_path, new_path, 1)

MANAGER.write_text(manager, encoding="utf-8")
print("Applied v4 diagnostic instrumentation: manifest-order live Period selection, MPD Period inventory, candidate/selection tracing, live batch index ranges, and init-context transition tracing.")
