<# Quarantine-apply for curated dedupe verdicts. DRY-RUN by default: prints and
   returns the move plan; -Apply performs Move-Item into the quarantine mirror.
   NEVER deletes. Idempotent: already-quarantined losers report
   skip-already-moved. Verdicts are hash-keyed, paths re-rooted from
   -ManifestRoot to -Root, so one curated sidecar replays against any tree that
   holds the same content (ax1p first, prod later).
   Spec: restfulbee docs/superpowers/specs/2026-07-04-intake-dedupe-design.md #>
param(
    [Parameter(Mandatory=$true)][string]$Manifest,
    [Parameter(Mandatory=$true)][string]$Verdicts,
    [Parameter(Mandatory=$true)][string]$ManifestRoot,
    [Parameter(Mandatory=$true)][string]$Root,
    [string]$QuarantineRoot,
    [switch]$Apply,
    [string]$LogCsv
)
$ErrorActionPreference = 'Stop'
if (-not $QuarantineRoot) { $QuarantineRoot = $Root.TrimEnd('\') + '-quarantine' }
function Re-Root([string]$path) {
    $mr = $ManifestRoot.TrimEnd('\') + '\'
    if (-not $path.StartsWith($mr, [System.StringComparison]::OrdinalIgnoreCase)) { return $null }
    return (Join-Path $Root $path.Substring($mr.Length))
}
$m = Get-Content -Path $Manifest -Raw | ConvertFrom-Json
$v = Get-Content -Path $Verdicts -Raw | ConvertFrom-Json
$byHash = @{}; foreach ($e in $v.verdicts) { $byHash[$e.hash] = $e }
$plan = New-Object System.Collections.ArrayList
$stats = @{ resolve=0; expected=0; uncurated=0; moves=0; skipped=0 }
foreach ($g in $m.groups) {
    $e = $byHash[$g.hash]
    if ($null -eq $e -or $null -eq $e.verdict) { $stats.uncurated++; continue }
    if ($e.verdict -eq 'expected') { $stats.expected++; continue }
    if ($e.verdict -ne 'resolve') { continue }
    $stats.resolve++
    $keep = Re-Root $e.keepPath
    $present = @($g.paths | ForEach-Object { Re-Root $_ } | Where-Object { $_ -and (Test-Path $_) })
    if (-not $keep -or -not (Test-Path $keep)) {
        [void]$plan.Add([pscustomobject]@{ hash=$g.hash; from=$e.keepPath; to=$null; action='skip-keeper-missing' })
        $stats.skipped++; continue
    }
    if ($present.Count -lt 2) {
        [void]$plan.Add([pscustomobject]@{ hash=$g.hash; from=$keep; to=$null; action='skip-single-copy' })
        $stats.skipped++; continue
    }
    foreach ($p in $g.paths) {
        $tp = Re-Root $p
        if (-not $tp -or $tp -eq $keep) { continue }
        $rel = $tp.Substring(($Root.TrimEnd('\') + '\').Length)
        $qp = Join-Path $QuarantineRoot $rel
        if (-not (Test-Path $tp)) {
            $action = 'skip-already-moved'
            if (-not (Test-Path $qp)) { $stats.skipped++ }   # neither side: nothing to do, still log
            [void]$plan.Add([pscustomobject]@{ hash=$g.hash; from=$tp; to=$qp; action=$action })
            continue
        }
        [void]$plan.Add([pscustomobject]@{ hash=$g.hash; from=$tp; to=$qp; action='move' })
        $stats.moves++
        if ($Apply) {
            New-Item -ItemType Directory -Path (Split-Path $qp) -Force | Out-Null
            Move-Item -LiteralPath $tp -Destination $qp
        }
    }
}
$mode = 'DRY-RUN'; if ($Apply) { $mode = 'APPLY' }
Write-Host ("apply-dedupe-verdicts [{0}]: groups={1} resolve={2} expected={3} uncurated={4} moves={5} skipped={6}" -f `
    $mode, $m.groups.Count, $stats.resolve, $stats.expected, $stats.uncurated, $stats.moves, $stats.skipped)
if ($LogCsv) {
    $stamp = (Get-Date).ToUniversalTime().ToString('o')
    $plan | Select-Object @{n='timestampUtc';e={$stamp}}, @{n='mode';e={$mode}}, hash, from, to, action |
        Export-Csv -Path $LogCsv -NoTypeInformation -Append
}
$plan
