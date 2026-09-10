using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace N_m3u8DL_RE.Util;

/// <summary>
/// Temporary diagnostics for the native fMP4 -> MPEG-TS mux path.
/// Prints first-chance exceptions whose stack is inside NativeFmp4TsMuxer,
/// without changing the muxer's control flow or swallowing behavior.
/// </summary>
internal static class NativeMuxFirstChanceDiagnostics
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        var stack = e.Exception.StackTrace;
        if (stack is null || !stack.Contains(nameof(NativeFmp4TsMuxer), StringComparison.Ordinal))
            return;

        Console.Error.WriteLine($"[PIPE-DIRECT] NativeFmp4TsMuxer first-chance exception: {e.Exception}");
        Console.Error.WriteLine($"[PIPE-DIRECT] NativeFmp4TsMuxer stack: {stack}");
    }
}
