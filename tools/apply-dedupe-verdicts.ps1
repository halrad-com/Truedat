<# Quarantine-apply for curated dedupe verdicts. DRY-RUN by default: prints and
   returns the move plan; -Apply performs Move-Item into the quarantine mirror.
   NEVER deletes. Idempotent: already-quarantined losers report
   skip-already-moved. Verdicts are hash-keyed, paths re-rooted from
   -ManifestRoot to -Root, so one curated sidecar replays against any tree that
   holds the same content (ax1p first, prod later).

   Plan-entry action vocabulary (each plan object is {hash;from;to;action}):
     move                  - loser present in root, planned/executed move to quarantine
     skip-already-moved    - loser already in quarantine (or absent both places); nothing to do
     skip-keeper-missing   - resolve verdict but the keeper path isn't on disk; group skipped
     skip-single-copy      - fewer than 2 copies present in root and no loser in quarantine
     conflict-dest-exists  - quarantine destination already occupied by a different file;
                             move is REFUSED to avoid clobbering it (from=root path, to=quarantine path)
     skip-unknown-verdict  - sidecar verdict is neither null, 'resolve', nor 'expected' (e.g. a typo);
                             operator intent could not be honored, surfaced instead of silently dropped
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
$stats = @{ resolve=0; expected=0; uncurated=0; moves=0; skipped=0; conflicts=0; unknownVerdicts=0 }
foreach ($g in $m.groups) {
    $e = $byHash[$g.hash]
    if ($null -eq $e -or $null -eq $e.verdict) { $stats.uncurated++; continue }
    if ($e.verdict -eq 'expected') { $stats.expected++; continue }
    if ($e.verdict -ne 'resolve') {
        Write-Warning ("apply-dedupe-verdicts: unknown verdict '{0}' for hash {1} - operator edit ignored" -f $e.verdict, $g.hash)
        $stats.unknownVerdicts++
        [void]$plan.Add([pscustomobject]@{ hash=$g.hash; from=$null; to=$null; action='skip-unknown-verdict' })
        continue
    }
    $stats.resolve++
    $keep = Re-Root $e.keepPath
    if (-not $keep -or -not (Test-Path $keep)) {
        [void]$plan.Add([pscustomobject]@{ hash=$g.hash; from=$e.keepPath; to=$null; action='skip-keeper-missing' })
        $stats.skipped++; continue
    }

    # Classify every path in the group (including keeper) independently -
    # presentInRoot / inQuarantine / absentBoth - so partial quarantine state
    # (some losers already moved, others still sitting in root) never masks
    # a loser that still needs to move.
    $presentInRootCount = 0
    $anyLoserInQuarantine = $false
    $classified = New-Object System.Collections.ArrayList
    foreach ($p in $g.paths) {
        $tp = Re-Root $p
        if (-not $tp) { continue }
        $isKeep = ($tp -eq $keep)
        $inRoot = Test-Path $tp
        if ($inRoot) { $presentInRootCount++ }
        $qp = $null; $inQuarantine = $false
        if (-not $isKeep) {
            $rel = $tp.Substring(($Root.TrimEnd('\') + '\').Length)
            $qp = Join-Path $QuarantineRoot $rel
            $inQuarantine = Test-Path $qp
            if ($inQuarantine) { $anyLoserInQuarantine = $true }
        }
        [void]$classified.Add([pscustomobject]@{ tp=$tp; isKeep=$isKeep; inRoot=$inRoot; qp=$qp; inQuarantine=$inQuarantine })
    }

    if ($presentInRootCount -lt 2 -and -not $anyLoserInQuarantine) {
        [void]$plan.Add([pscustomobject]@{ hash=$g.hash; from=$keep; to=$null; action='skip-single-copy' })
        $stats.skipped++; continue
    }

    foreach ($c in $classified) {
        if ($c.isKeep) { continue }
        if ($c.inRoot) {
            if (Test-Path $c.qp) {
                # Quarantine destination is already occupied (e.g. loser was
                # quarantined on a prior run and a different file has since
                # reappeared at the same root path). Never overwrite it -
                # refuse the move and surface the conflict for the operator.
                [void]$plan.Add([pscustomobject]@{ hash=$g.hash; from=$c.tp; to=$c.qp; action='conflict-dest-exists' })
                $stats.conflicts++
                continue
            }
            [void]$plan.Add([pscustomobject]@{ hash=$g.hash; from=$c.tp; to=$c.qp; action='move' })
            $stats.moves++
            if ($Apply) {
                New-Item -ItemType Directory -Path (Split-Path $c.qp) -Force | Out-Null
                Move-Item -LiteralPath $c.tp -Destination $c.qp
            }
        } else {
            # inQuarantine or absentBoth - either way there's nothing left in
            # root to move; log it as already-moved to keep the audit trail.
            [void]$plan.Add([pscustomobject]@{ hash=$g.hash; from=$c.tp; to=$c.qp; action='skip-already-moved' })
            $stats.skipped++
        }
    }
}
$mode = 'DRY-RUN'; if ($Apply) { $mode = 'APPLY' }
$summary = ("apply-dedupe-verdicts [{0}]: groups={1} resolve={2} expected={3} uncurated={4} moves={5} skipped={6} conflicts={7}" -f `
    $mode, $m.groups.Count, $stats.resolve, $stats.expected, $stats.uncurated, $stats.moves, $stats.skipped, $stats.conflicts)
if ($stats.unknownVerdicts -gt 0) { $summary += (" unknownVerdicts={0}" -f $stats.unknownVerdicts) }
Write-Host $summary
if ($LogCsv) {
    $logDir = Split-Path -Parent $LogCsv
    if ($logDir -and -not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
    $stamp = (Get-Date).ToUniversalTime().ToString('o')
    $plan | Select-Object @{n='timestampUtc';e={$stamp}}, @{n='mode';e={$mode}}, hash, from, to, action |
        Export-Csv -Path $LogCsv -NoTypeInformation -Append
}
$plan
