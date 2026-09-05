using N_m3u8DL_RE.Common.Enum;
using N_m3u8DL_RE.Common.Log;
using N_m3u8DL_RE.Parser.Config;

namespace N_m3u8DL_RE.Parser.Processor.DASH;

/// <summary>
/// MPD自动补充Namespace，并可选保存每次收到的直播MPD用于诊断。
/// </summary>
public class DefaultDASHContentProcessor : ContentProcessor
{
    private static readonly Dictionary<string, string> NamespaceMap = new()
    {
        ["cenc"] = "urn:mpeg:cenc:2013",
        ["mspr"] = "urn:microsoft:playready",
        ["mas"] = "urn:marlin:mas:1-0:services:schemas:mpd",
    };
    
    public override bool CanProcess(ExtractorType extractorType, string mpdContent, ParserConfig parserConfig)
    {
        if (extractorType != ExtractorType.MPEG_DASH) return false;

        return IsCaptureEnabled() || NamespaceMap.Keys.Any(x => IsMissingNs(mpdContent, x));
    }

    public override string Process(string mpdContent, ParserConfig parserConfig)
    {
        CaptureManifest(mpdContent);

        var missingNamespaceKeys = NamespaceMap.Keys.Where(x => IsMissingNs(mpdContent, x)).ToList();
        if (missingNamespaceKeys.Count == 0)
            return mpdContent;

        Logger.InfoMarkUp("[gray]Namespace missing, try fix...[/]");
        var missingNamespaceDfns = missingNamespaceKeys.Select(key => $"xmlns:{key}=\"{NamespaceMap[key]}\"");
        var declarations = string.Join(" ", missingNamespaceDfns);
        return ReplaceFirst(mpdContent, "<MPD ", $"<MPD {declarations} ");
    }

    private static bool IsCaptureEnabled()
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("N_M3U8DL_RE_MANIFEST_CAPTURE_DIR"));
    }

    private static void CaptureManifest(string mpdContent)
    {
        var directory = Environment.GetEnvironmentVariable("N_M3U8DL_RE_MANIFEST_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;

        try
        {
            Directory.CreateDirectory(directory);
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss.fff'Z'");
            var sequence = Interlocked.Increment(ref _captureSequence);
            var path = Path.Combine(directory, $"{timestamp}_{sequence:D6}.mpd");
            File.WriteAllText(path, mpdContent);
        }
        catch
        {
            // Diagnostic capture must never interfere with stream processing.
        }
    }

    private static long _captureSequence;

    private static bool IsMissingNs(string rawText, string tag)
    {
        return !rawText.Contains($"xmlns:{tag}") && rawText.Contains($"<{tag}:");
    }

    private static string ReplaceFirst(string source, string oldValue, string newValue)
    {
        var index = source.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? source :
            source.Remove(index, oldValue.Length).Insert(index, newValue);
    }
}