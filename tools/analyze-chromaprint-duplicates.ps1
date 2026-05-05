<#
.SYNOPSIS
    Reports how many tracks in mbxmoods.json share a chromaprint — i.e. how
    many cross-encoding duplicates would collapse into a single library entry
    if we used chromaprint as the primary key instead of file path.

.DESCRIPTION
    Helps decide between three identity strategies:

      1. audioStreamSha256 as primary key   - byte-content hash (post metadata
                                              strip). Fast, managed-only, no
                                              subprocess. Misses cross-encoding
                                              duplicates.
      2. Chromaprint as primary key (fpcalc) - perceptual hash, catches
                                              cross-encoding duplicates.
                                              Brittle subprocess, slower.
      3. Chromaprint via P/Invoke chromaprint.dll - same matching, no
                                              subprocess. Real engineering.

    If your library has many duplicate groups, (2) or (3) earn their keep.
    If groups are rare, (1) is the cleanest choice.

.PARAMETER Path
    Path to mbxmoods.json. Defaults to ./mbxmoods.json.

.PARAMETER ShowSample
    How many duplicate groups to print details for (default 10).
    Set to 0 to suppress the sample listing.

.EXAMPLE
    pwsh -File tools/analyze-chromaprint-duplicates.ps1 -Path C:\Music\mbxmoods.json

.EXAMPLE
    pwsh -File tools/analyze-chromaprint-duplicates.ps1 -ShowSample 25
#>

[CmdletBinding()]
param(
    [string] $Path = "mbxmoods.json",
    [int]    $ShowSample = 10
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "mbxmoods.json not found at: $Path"
    exit 1
}

Write-Host "Reading $Path..."
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$json = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
$sw.Stop()
Write-Host ("  loaded in {0:F1}s" -f $sw.Elapsed.TotalSeconds)

if (-not $json.tracks) {
    Write-Error "JSON has no 'tracks' object — is this an mbxmoods.json file?"
    exit 1
}

$tracks = $json.tracks
$total = 0
$withChroma = 0
$withSha = 0

# Build chromaprint -> [paths] index
$byChroma = @{}
$byShaSample = @{}  # parallel index for cross-check

foreach ($prop in $tracks.PSObject.Properties) {
    $total++
    $path = $prop.Name
    $entry = $prop.Value

    $cp = $null
    if ($entry.PSObject.Properties.Name -contains "chromaprint" -and $entry.chromaprint) {
        $cp = [string] $entry.chromaprint
        $withChroma++
    }

    $sha = $null
    if ($entry.PSObject.Properties.Name -contains "audioStreamSha256" -and $entry.audioStreamSha256) {
        $sha = [string] $entry.audioStreamSha256
        $withSha++
    }

    if ($cp) {
        if (-not $byChroma.ContainsKey($cp)) { $byChroma[$cp] = New-Object System.Collections.ArrayList }
        [void] $byChroma[$cp].Add(@{ Path = $path; Sha = $sha })
    }
}

Write-Host ""
Write-Host "=== Library shape ==="
Write-Host ("  Total tracks:                {0,8:N0}" -f $total)
Write-Host ("  With chromaprint populated:  {0,8:N0}  ({1,5:P1})" -f $withChroma, ($(if ($total) { $withChroma / $total } else { 0 })))
Write-Host ("  With audioStreamSha256:      {0,8:N0}  ({1,5:P1})" -f $withSha, ($(if ($total) { $withSha / $total } else { 0 })))

if ($withChroma -eq 0) {
    Write-Host ""
    Write-Warning "No tracks have a chromaprint populated. Either fpcalc wasn't bundled when these were scanned, or chromaprint was already cleared. Can't run the duplicate analysis."
    exit 0
}

# Bucket by chromaprint count
$sizes = $byChroma.Values | ForEach-Object { $_.Count }
$uniqueChromas = $byChroma.Count
$dupGroups = @($byChroma.GetEnumerator() | Where-Object { $_.Value.Count -gt 1 })
$tracksInDupGroups = ($dupGroups | ForEach-Object { $_.Value.Count } | Measure-Object -Sum).Sum
$tracksThatWouldCollapse = $tracksInDupGroups - $dupGroups.Count

Write-Host ""
Write-Host "=== Chromaprint analysis ==="
Write-Host ("  Unique chromaprints:           {0,8:N0}" -f $uniqueChromas)
Write-Host ("  Duplicate groups (size > 1):   {0,8:N0}" -f $dupGroups.Count)
Write-Host ("  Tracks in duplicate groups:    {0,8:N0}  ({1,5:P1} of all chromaprint-having tracks)" -f $tracksInDupGroups, ($(if ($withChroma) { $tracksInDupGroups / $withChroma } else { 0 })))
Write-Host ("  Tracks that would collapse:    {0,8:N0}  (entries lost vs kept if chromaprint were the key)" -f $tracksThatWouldCollapse)

if ($dupGroups.Count -eq 0) {
    Write-Host ""
    Write-Host "==> NO CROSS-ENCODING DUPLICATES."
    Write-Host "    audioStreamSha256 catches every match chromaprint would. Use it as primary key."
    Write-Host "    fpcalc adds cost without benefit for this library."
    exit 0
}

# Bucket the duplicate groups by size
$bySize = $dupGroups | Group-Object { $_.Value.Count } | Sort-Object @{ Expression = { [int] $_.Name }; Descending = $false }
Write-Host ""
Write-Host "  Group size distribution:"
foreach ($g in $bySize) {
    Write-Host ("    size = {0,3}: {1,6:N0} groups" -f [int]$g.Name, $g.Count)
}

# Detect "false positives" — same chromaprint but ALSO same audioStreamSha256
# means it's literally the same file bytes (e.g. a path-cache duplication or
# user has the file at two paths). Those don't tell us anything about
# cross-encoding matching capability.
$realCrossEncoding = 0
$pureFileDup = 0
foreach ($kv in $dupGroups) {
    $shasInGroup = ($kv.Value | ForEach-Object { $_.Sha } | Where-Object { $_ } | Sort-Object -Unique)
    if ($shasInGroup.Count -gt 1) {
        $realCrossEncoding++
    } elseif ($shasInGroup.Count -eq 1) {
        $pureFileDup++
    }
}

Write-Host ""
Write-Host "=== Of those duplicate groups ==="
Write-Host ("  Real cross-encoding (different audioStreamSha256, same chromaprint): {0,6:N0} groups" -f $realCrossEncoding)
Write-Host ("  Pure file duplication (same audioStreamSha256, same path-set):       {0,6:N0} groups" -f $pureFileDup)
Write-Host ("                                                                        ----------"
)
Write-Host ("  (groups missing audioStreamSha256 on members are excluded above)")

Write-Host ""
if ($realCrossEncoding -eq 0) {
    Write-Host "==> All chromaprint duplicates are also audioStreamSha256 duplicates."
    Write-Host "    audioStreamSha256 alone catches every match. fpcalc adds no value."
} elseif ($realCrossEncoding -lt 10) {
    Write-Host "==> A handful of cross-encoding duplicates ($realCrossEncoding groups)."
    Write-Host "    audioStreamSha256 is probably sufficient. fpcalc earns its keep only if"
    Write-Host "    deduplicating these is important to you."
} else {
    Write-Host "==> Meaningful cross-encoding duplication ($realCrossEncoding groups)."
    Write-Host "    Chromaprint as primary key would actually consolidate the library."
}

if ($ShowSample -gt 0 -and $realCrossEncoding -gt 0) {
    Write-Host ""
    Write-Host "=== Sample of cross-encoding duplicate groups (first $ShowSample) ==="
    $shown = 0
    foreach ($kv in $dupGroups) {
        if ($shown -ge $ShowSample) { break }
        $shasInGroup = ($kv.Value | ForEach-Object { $_.Sha } | Where-Object { $_ } | Sort-Object -Unique)
        if ($shasInGroup.Count -le 1) { continue }
        $shown++
        Write-Host ""
        Write-Host ("  chromaprint = {0}..." -f $kv.Key.Substring(0, [Math]::Min(40, $kv.Key.Length)))
        foreach ($t in $kv.Value) {
            $shaShort = if ($t.Sha) { $t.Sha.Substring(0, 12) + "..." } else { "(no sha256)" }
            Write-Host ("    [{0}] {1}" -f $shaShort, $t.Path)
        }
    }
}
