from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANAGER = ROOT / "src/N_m3u8DL-RE/DownloadManager/SimpleLiveRecordManager2.cs"

manager = MANAGER.read_text(encoding="utf-8-sig")

# The live recorder keeps distinct encrypted/decrypted init contexts in
# init-context-* directories so KID/init changes cannot collide. StreamRelay's
# HLS scanner, however, expects the active _init_dec.mp4 and media fragments to
# share the track directory. Expose the newest decrypted init there as a
# symlink while retaining the isolated real file underneath its context dir.
needle = '''                        if (dResult)\n                        {\n                            FileDic[streamSpec.Playlist.MediaInit]!.ActualFilePath = dec;\n                        }'''
replacement = '''                        if (dResult)\n                        {\n                            FileDic[streamSpec.Playlist.MediaInit]!.ActualFilePath = dec;\n                            var activeInitAlias = Path.Combine(tmpDir, "_init_dec.mp4");\n                            try\n                            {\n                                if (File.Exists(activeInitAlias) || Directory.Exists(activeInitAlias))\n                                    File.Delete(activeInitAlias);\n                                File.CreateSymbolicLink(activeInitAlias, dec);\n                            }\n                            catch\n                            {\n                                File.Copy(dec, activeInitAlias, true);\n                            }\n                        }'''
if needle not in manager:
    raise SystemExit("Could not find live decryption block; manager patch may have changed.")
manager = manager.replace(needle, replacement, 1)

MANAGER.write_text(manager, encoding="utf-8")
print("Added active _init_dec.mp4 alias in each track directory for StreamRelay HLS discovery.")
