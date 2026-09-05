from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DASH = ROOT / "src/N_m3u8DL-RE.Parser/Extractor/DASHExtractor2.cs"
MANAGER = ROOT / "src/N_m3u8DL-RE/DownloadManager/SimpleLiveRecordManager2.cs"

# Keep the live DASH Period handoff surgical. The previous build-time recorder
# resilience injection was too invasive and could produce an unplayable stream.
dash = DASH.read_text(encoding="utf-8-sig")

old_index = '                    var _index = streamList.FindIndex(_f => _f.PeriodId != streamSpec.PeriodId && _f.GroupId == streamSpec.GroupId && _f.Resolution == streamSpec.Resolution && _f.MediaType == streamSpec.MediaType);'
new_index = '''                    var _index = streamList.FindIndex(_f =>
                        _f.PeriodId != streamSpec.PeriodId &&
                        _f.MediaType == streamSpec.MediaType &&
                        _f.Resolution == streamSpec.Resolution &&
                        (_f.GroupId == streamSpec.GroupId || (isLive && IsSameLogicalLiveTrack(_f, streamSpec))));'''
if old_index in dash:
    dash = dash.replace(old_index, new_index, 1)

old_live = '''                        if (isLive)
                        {
                            // 直播，这种情况直接略过新的
                        }'''
new_live = '''                        if (isLive)
                        {
                            // Live MPDs can overlap Periods at ad/content boundaries.
                            // Prefer the newest Period so refreshes follow the active timeline.
                            Logger.Debug($"[DASH] Prefer newest live Period for {streamSpec.GroupId} ({streamSpec.MediaType}, {streamSpec.Resolution})");
                            streamList[_index] = streamSpec;
                        }'''
if old_live in dash:
    dash = dash.replace(old_live, new_live, 1)

DASH.write_text(dash, encoding="utf-8")

# A live DASH Period can change the initialization segment and/or default_KID
# without ending the recording. Reset only the fMP4 initialization/decryption
# context so the next batch downloads the new init and resolves its key.
manager = MANAGER.read_text(encoding="utf-8-sig")

if "string observedPeriodId = \"\";" not in manager:
    old_state = '''        bool initDownloaded = false; // 是否下载过init文件
        ConcurrentDictionary<MediaSegment, DownloadResult?> FileDic = new();'''
    new_state = '''        bool initDownloaded = false; // 是否下载过init文件
        string observedPeriodId = "";
        string observedInitUrl = "";
        string observedMpdKid = "";
        ConcurrentDictionary<MediaSegment, DownloadResult?> FileDic = new();'''
    if old_state not in manager:
        raise RuntimeError("SimpleLiveRecordManager2.cs init state anchor not found")
    manager = manager.replace(old_state, new_state, 1)

    old_batch = '''            var segmentsDuration = segments.Sum(s => s.Duration);
            Logger.DebugMarkUp(string.Join(",", segments.Select(sss => GetSegmentName(sss, false, false))));

            // 下载init'''
    new_batch = '''            var segmentsDuration = segments.Sum(s => s.Duration);

            // Live DASH ad/content boundaries can switch Period, initialization
            // segment, and/or default_KID while the recorder remains alive.
            // The old init/decryption context must not be reused across that boundary.
            if (StreamExtractor.ExtractorType == ExtractorType.DASH)
            {
                var incomingPeriodId = streamSpec.PeriodId ?? "";
                var incomingInitUrl = streamSpec.Playlist?.MediaInit?.Url ?? "";
                var incomingMpdKid = streamSpec.Playlist?.MediaInit?.EncryptInfo.KID ?? "";
                if (!string.IsNullOrEmpty(observedPeriodId) &&
                    (incomingPeriodId != observedPeriodId ||
                     incomingInitUrl != observedInitUrl ||
                     (!string.IsNullOrEmpty(incomingMpdKid) && incomingMpdKid != observedMpdKid)))
                {
                    Logger.WarnMarkUp($"[LIVE] DASH Period/init change: {observedPeriodId} -> {incomingPeriodId}; resetting fMP4 init/decryption context.");
                    initDownloaded = false;
                    mp4InitFile = "";
                    currentKID = "";
                }
                observedPeriodId = incomingPeriodId;
                observedInitUrl = incomingInitUrl;
                observedMpdKid = incomingMpdKid;
            }

            Logger.DebugMarkUp(string.Join(",", segments.Select(sss => GetSegmentName(sss, false, false))));

            // 下载init'''
    if old_batch not in manager:
        raise RuntimeError("SimpleLiveRecordManager2.cs live batch anchor not found")
    manager = manager.replace(old_batch, new_batch, 1)

MANAGER.write_text(manager, encoding="utf-8")
print("Applied surgical live DASH Period handoff plus Period-scoped fMP4 init/KID reset; recorder resilience injection remains disabled.")
