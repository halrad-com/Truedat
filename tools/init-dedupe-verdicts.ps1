<# Bootstrap or merge the dedupe verdicts sidecar from truedat's
   mbxmoods-duplicates.json. Existing verdicts always win by hash — that IS the
   suppression mechanism: 'expected' rulings survive every re-detection.
   Spec: restfulbee docs/superpowers/specs/2026-07-04-intake-dedupe-design.md #>
param(
    [Parameter(Mandatory=$true)][string]$Manifest,
    [Parameter(Mandatory=$true)][string]$Verdicts
)
$ErrorActionPreference = 'Stop'
$m = Get-Content -Path $Manifest -Raw | ConvertFrom-Json
$existing = @{}
if (Test-Path $Verdicts) {
    $v = Get-Content -Path $Verdicts -Raw | ConvertFrom-Json
    foreach ($e in $v.verdicts) { $existing[$e.hash] = $e }
}
$out = @()
foreach ($g in $m.groups) {
    if ($existing.ContainsKey($g.hash)) { $out += $existing[$g.hash]; continue }
    $keep = $null
    if ($g.tier -eq 'exact') { $keep = $g.keeper }   # probable: keeper is a hint, no prefill
    $out += [pscustomobject]@{ hash = $g.hash; tier = $g.tier; verdict = $null; keepPath = $keep }
}
# keep curated history for hashes no longer detected (paths may have moved)
foreach ($h in $existing.Keys) {
    if (-not ($m.groups | Where-Object { $_.hash -eq $h })) { $out += $existing[$h] }
}
$doc = [pscustomobject]@{ version = 1; verdicts = $out }
$doc | ConvertTo-Json -Depth 5 | Set-Content -Path $Verdicts -Encoding UTF8
$counts = ($out | Group-Object { if ($null -eq $_.verdict) { 'uncurated' } else { $_.verdict } })
Write-Host ("init-dedupe-verdicts: entries={0} ({1})" -f $out.Count, (($counts | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' '))
