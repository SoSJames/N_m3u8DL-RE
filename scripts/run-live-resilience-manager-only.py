from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANAGER = ROOT / "src/N_m3u8DL-RE/DownloadManager/SimpleLiveRecordManager2.cs"
DASH = ROOT / "src/N_m3u8DL-RE.Parser/Extractor/DASHExtractor2.cs"


def rep(text, old, new, label):
    if old not in text:
        raise RuntimeError(f"Patch target not found: {label}")
    return text.replace(old, new, 1)

source = (ROOT / "scripts/apply-live-resilience-v2.py").read_text(encoding="utf-8")
manager_patch = source[source.index("text = MANAGER.read_text") :]
exec(compile(manager_patch, "scripts/apply-live-resilience-v2.py", "exec"), globals(), locals())
