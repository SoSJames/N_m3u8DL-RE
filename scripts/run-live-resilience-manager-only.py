from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DASH = ROOT / "src/N_m3u8DL-RE.Parser/Extractor/DASHExtractor2.cs"

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
print("Applied surgical live DASH Period handoff patch; recorder resilience injection disabled.")
