from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DASH = ROOT / "src/N_m3u8DL-RE.Parser/Extractor/DASHExtractor2.cs"
MANAGER = ROOT / "src/N_m3u8DL-RE/DownloadManager/SimpleLiveRecordManager2.cs"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise RuntimeError(f"Patch target not found: {label}")
    return text.replace(old, new, 1)


# ---------------------------------------------------------------------------
# DASH extractor: use manifest order as the authoritative order for overlapping
# live Periods. Spectrum's AD Period IDs are not safely orderable as strings:
# e.g. <base>-AD-3 compares greater than <base>, even when the latter is the
# resumed content Period. Selecting by lexicographic PeriodId can therefore
# remain stuck on an ad Period after the ad has ended.
# ---------------------------------------------------------------------------
dash = DASH.read_text(encoding="utf-8-sig")
dash = replace_once(
    dash,
    '                    var _index = streamList.FindIndex(_f => _f.PeriodId != streamSpec.PeriodId && _f.GroupId == streamSpec.GroupId && _f.Resolution == streamSpec.Resolution && _f.MediaType == streamSpec.MediaType);',
    '''                    var _index = streamList.FindIndex(_f =>
                        _f.PeriodId != streamSpec.PeriodId &&
                        _f.MediaType == streamSpec.MediaType &&
                        _f.Resolution == streamSpec.Resolution &&
                        (_f.GroupId == streamSpec.GroupId || (isLive && IsSameLogicalLiveTrack(_f, streamSpec))));''',
    "DASH logical live Period matching",
)

dash = replace_once(
    dash,
    '''                        if (isLive)
                        {
                            // 直播，这种情况直接略过新的
                        }''',
    '''                        if (isLive)
                        {
                            // Live MPDs can overlap Periods at ad/content boundaries.
                            // The MPD's Period element order is the authoritative timeline
                            // order; do not compare Spectrum's synthetic -AD-N IDs as strings.
                            var oldSpec = streamList[_index];
                            var oldInit = oldSpec.Playlist?.MediaInit?.Url ?? "";
                            var newInit = streamSpec.Playlist?.MediaInit?.Url ?? "";
                            var oldKid = oldSpec.Playlist?.MediaInit?.EncryptInfo.KID ?? "";
                            var newKid = streamSpec.Playlist?.MediaInit?.EncryptInfo.KID ?? "";
                            var newParts = streamSpec.Playlist?.MediaParts.FirstOrDefault()?.MediaSegments.Count ?? 0;
                            Logger.Debug($"[DASH][LIVE-TRANSITION] {streamSpec.MediaType} {streamSpec.Resolution} Group={streamSpec.GroupId} Period {oldSpec.PeriodId} -> {streamSpec.PeriodId}; Init {oldInit} -> {newInit}; KID {oldKid} -> {newKid}; Parts={newParts}");
                            streamList[_index] = streamSpec;
                        }''',
    "DASH live Period replacement",
)

# Preserve manifest order explicitly. This is important for AD -> content when
# the resumed content Period has an ID that is lexicographically smaller than
# the preceding synthetic -AD-N Period.
dash = replace_once(
    dash,
    '                var matched = match.OrderByDescending(n => n.PeriodId, StringComparer.Ordinal).First();',
    '''                var matched = match.Last();
                Logger.Debug($"[DASH][LIVE-SELECT] Group={streamSpec.GroupId} Type={streamSpec.MediaType} Res={streamSpec.Resolution} Candidates={string.Join(" -> ", match.Select(n => n.PeriodId))} Selected={matched.PeriodId}");''',
    "manifest-order live Period selection",
)

# Log the actual Period sequence seen on every MPD refresh. This lets the
# StreamRelay diagnostic log show the complete content -> AD -> content chain,
# not just the Period that happened to win track matching.
dash = replace_once(
    dash,
    '        var periods = mpdElement.Elements().Where(e => e.Name.LocalName == "Period");',
    '''        var periods = mpdElement.Elements().Where(e => e.Name.LocalName == "Period").ToList();
        if (isLive)
        {
            var periodSummary = string.Join(" | ", periods.Select((p, i) =>
            {
                var id = p.Attribute("id")?.Value ?? "";
                var start = p.Attribute("start")?.Value ?? "";
                var duration = p.Attribute("duration")?.Value ?? "";
                return $"#{i}:{id} start={start} duration={duration}";
            }));
            Logger.Debug($"[DASH][LIVE-MPD] Periods={periodSummary}");
        }''',
    "live MPD Period sequence diagnostics",
)

DASH.write_text(dash, encoding="utf-8")


# ---------------------------------------------------------------------------
# Live recorder: separate Period identity from initialization identity. A
# Period-only transition reusing the same init must not reset/decrypt the init.
# A changed init URL or MPD KID gets a new isolated init context.
# ---------------------------------------------------------------------------
manager = MANAGER.read_text(encoding="utf-8-sig")
manager = replace_once(
    manager,
    '''        bool initDownloaded = false; // 是否下载过init文件
        ConcurrentDictionary<MediaSegment, DownloadResult?> FileDic = new();''',
    '''        bool initDownloaded = false; // 是否下载过init文件
        string observedPeriodId = "";
        string observedInitUrl = "";
        string observedMpdKid = "";
        string initContextKey = "";
        ConcurrentDictionary<MediaSegment, DownloadResult?> FileDic = new();''',
    "live recorder transition state",
)

manager = replace_once(
    manager,
    '''            var segmentsDuration = segments.Sum(s => s.Duration);
            Logger.DebugMarkUp(string.Join(",", segments.Select(sss => GetSegmentName(sss, false, false))));

            // 下载init''',
    '''            var segmentsDuration = segments.Sum(s => s.Duration);

            var incomingPeriodId = streamSpec.PeriodId ?? "";
            var incomingInitUrl = streamSpec.Playlist?.MediaInit?.Url ?? "";
            var incomingMpdKid = streamSpec.Playlist?.MediaInit?.EncryptInfo.KID ?? "";
            var initUrlChanged = incomingInitUrl != observedInitUrl;
            var mpdKidChanged = incomingMpdKid != observedMpdKid;
            var periodChanged = !string.IsNullOrEmpty(observedPeriodId) && incomingPeriodId != observedPeriodId;
            var firstIndex = segments.FirstOrDefault()?.Index;
            var lastIndex = segments.LastOrDefault()?.Index;
            Logger.Debug($"[LIVE][BATCH] {streamSpec.MediaType} Group={streamSpec.GroupId} Period={incomingPeriodId} Init={incomingInitUrl} KID={incomingMpdKid} Parts={segments.Count()} Index={firstIndex}->{lastIndex}");
            if (periodChanged)
            {
                Logger.WarnMarkUp($"[LIVE][PERIOD] {streamSpec.MediaType} Group={streamSpec.GroupId} {observedPeriodId} -> {incomingPeriodId}; InitChanged={initUrlChanged}; KIDChanged={mpdKidChanged}");
            }
            if (initUrlChanged || mpdKidChanged)
            {
                Logger.WarnMarkUp($"[LIVE][INIT-CONTEXT] {streamSpec.MediaType} Group={streamSpec.GroupId} init/KID changed; creating a new fMP4 init context.");
                initDownloaded = false;
                mp4InitFile = "";
                currentKID = "";
                initContextKey = "";
            }
            else if (periodChanged)
            {
                Logger.Debug($"[LIVE][INIT-CONTEXT] Period-only transition; reusing init context for {incomingPeriodId}.");
            }
            observedPeriodId = incomingPeriodId;
            observedInitUrl = incomingInitUrl;
            observedMpdKid = incomingMpdKid;

            Logger.DebugMarkUp(string.Join(",", segments.Select(sss => GetSegmentName(sss, false, false))));

            // 下载init''',
    "live batch diagnostics and context handling",
)

manager = replace_once(
    manager,
    '                var path = Path.Combine(tmpDir, "_init.mp4.tmp");',
    '''                if (string.IsNullOrEmpty(initContextKey))
                {
                    var contextBytes = System.Text.Encoding.UTF8.GetBytes($"{incomingInitUrl}\\n{incomingMpdKid}");
                    initContextKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contextBytes)).ToLowerInvariant()[..16];
                }
                var initDir = Path.Combine(tmpDir, $"init-context-{initContextKey}");
                Directory.CreateDirectory(initDir);
                Logger.Debug($"[LIVE][INIT-CONTEXT] Using {initDir} for Period={incomingPeriodId} Init={incomingInitUrl} KID={incomingMpdKid}");
                var path = Path.Combine(initDir, "_init.mp4.tmp");''',
    "context-specific init path",
)

# Expose the active decrypted init using the conventional filename expected by
# StreamRelay while keeping the real init files isolated by context directory.
manager = replace_once(
    manager,
    '''                        if (dResult)
                        {
                            FileDic[streamSpec.Playlist.MediaInit]!.ActualFilePath = dec;
                        }''',
    '''                        if (dResult)
                        {
                            FileDic[streamSpec.Playlist.MediaInit]!.ActualFilePath = dec;
                            var activeInitAlias = Path.Combine(tmpDir, "_init_dec.mp4");
                            try
                            {
                                if (File.Exists(activeInitAlias) || Directory.Exists(activeInitAlias))
                                    File.Delete(activeInitAlias);
                                File.CreateSymbolicLink(activeInitAlias, dec);
                            }
                            catch
                            {
                                File.Copy(dec, activeInitAlias, true);
                            }
                            Logger.Debug($"[LIVE][INIT-DECRYPT] Period={incomingPeriodId} KID={currentKID} decrypted={dec} alias={activeInitAlias}");
                        }''',
    "active decrypted init alias",
)

MANAGER.write_text(manager, encoding="utf-8")
print("Applied live DASH v4 diagnostics and handoff handling: manifest-order Period selection, explicit content/AD/content transition logging, Period-vs-init identity separation, isolated init contexts, and StreamRelay-compatible active init alias.")
