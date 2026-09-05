using N_m3u8DL_RE.Common.Enum;
using N_m3u8DL_RE.Parser.Config;

namespace N_m3u8DL_RE.Parser.Processor;

/// <summary>
/// Diagnostic processor that preserves every live DASH MPD received by the extractor.
/// Enable by setting N_M3U8DL_RE_MANIFEST_CAPTURE_DIR to a writable directory.
/// The processor is deliberately transparent: it always returns the original content.
/// </summary>
public sealed class LiveManifestCaptureProcessor : ContentProcessor
{
    private static long _sequence;

    public override bool CanProcess(ExtractorType extractorType, string rawText, ParserConfig parserConfig)
    {
        return extractorType == ExtractorType.MPEG_DASH
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("N_M3U8DL_RE_MANIFEST_CAPTURE_DIR"));
    }

    public override string Process(string rawText, ParserConfig parserConfig)
    {
        var directory = Environment.GetEnvironmentVariable("N_M3U8DL_RE_MANIFEST_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            return rawText;

        try
        {
            Directory.CreateDirectory(directory);

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss.fff'Z'");
            var sequence = Interlocked.Increment(ref _sequence);
            var path = Path.Combine(directory, $"{timestamp}_{sequence:D6}.mpd");

            File.WriteAllText(path, rawText);
        }
        catch
        {
            // Manifest capture is diagnostic only and must never interfere with playback.
        }

        return rawText;
    }
}
