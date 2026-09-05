param(
    [Parameter(Mandatory=$true)]
    [string]$LogPath,

    [int]$ContextLines = 20,

    [int]$MaxMatchesPerPattern = 200
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $LogPath)) {
    throw "Log file not found: $LogPath"
}

$patterns = @(
    '\[DASH\]\[LIVE-TRANSITION\]',
    '\[LIVE\]\[BATCH\]',
    '\[LIVE\] DASH Period/init change',
    '\[StreamRelay\] Started acquisition',
    'Watchdog',
    'watchdog',
    '\bERROR\b',
    '\bFATAL\b',
    '\bException\b',
    'Unhandled',
    'Process exited',
    'Killed',
    'SIGTERM',
    'SIGKILL'
)

$lines = Get-Content -LiteralPath $LogPath
$hits = New-Object System.Collections.Generic.List[object]

for ($i = 0; $i -lt $lines.Count; $i++) {
    foreach ($pattern in $patterns) {
        if ($lines[$i] -match $pattern) {
            $hits.Add([pscustomobject]@{ Line = $i + 1; Pattern = $pattern })
            break
        }
    }
}

$base = [IO.Path]::GetFileNameWithoutExtension($LogPath)
$outDir = Join-Path ([IO.Path]::GetDirectoryName((Resolve-Path -LiteralPath $LogPath))) 'log-slices'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# 1. Compact event index.
$indexPath = Join-Path $outDir "$base-events.txt"
$hits | ForEach-Object {
    "{0}: {1}" -f $_.Line, $_.Pattern
} | Set-Content -LiteralPath $indexPath -Encoding UTF8

# 2. Context windows around significant events, de-duplicated when windows overlap.
$windows = New-Object System.Collections.Generic.List[object]
foreach ($hit in $hits) {
    $start = [Math]::Max(1, $hit.Line - $ContextLines)
    $end = [Math]::Min($lines.Count, $hit.Line + $ContextLines)
    $windows.Add([pscustomobject]@{ Start=$start; End=$end; Hit=$hit.Line; Pattern=$hit.Pattern })
}

$merged = @()
foreach ($w in ($windows | Sort-Object Start,End)) {
    if ($merged.Count -eq 0 -or $w.Start -gt ($merged[-1].End + 1)) {
        $merged += [pscustomobject]@{ Start=$w.Start; End=$w.End; Hits=@($w) }
    } else {
        $last = $merged[-1]
        $last.End = [Math]::Max($last.End, $w.End)
        $last.Hits += $w
    }
}

$contextPath = Join-Path $outDir "$base-context.txt"
$writer = New-Object System.IO.StreamWriter($contextPath, $false, [Text.UTF8Encoding]::new($false))
try {
    foreach ($window in $merged) {
        $writer.WriteLine(('=' * 90))
        $writer.WriteLine("Lines $($window.Start)-$($window.End)")
        foreach ($h in $window.Hits) {
            $writer.WriteLine("MATCH line $($h.Hit): $($h.Pattern)")
        }
        $writer.WriteLine(('=' * 90))
        for ($n = $window.Start; $n -le $window.End; $n++) {
            $writer.WriteLine(('{0,8}: {1}' -f $n, $lines[$n - 1]))
        }
        $writer.WriteLine()
    }
} finally {
    $writer.Dispose()
}

# 3. Last N relevant matches, useful when a run is extremely noisy.
$tailPath = Join-Path $outDir "$base-relevant-tail.txt"
$hits | Select-Object -Last $MaxMatchesPerPattern | ForEach-Object {
    "{0,8}: {1}" -f $_.Line, $lines[$_.Line - 1]
} | Set-Content -LiteralPath $tailPath -Encoding UTF8

Write-Host "Created:" -ForegroundColor Green
Write-Host "  $indexPath"
Write-Host "  $contextPath"
Write-Host "  $tailPath"
Write-Host "Matches: $($hits.Count); source lines: $($lines.Count)"

# Standalone workflow trigger marker.
