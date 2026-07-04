<# Bootstrap or merge the dedupe verdicts sidecar from truedat's
   mbxmoods-duplicates.json. Existing verdicts always win by key — that IS the
   suppression mechanism: 'expected' rulings survive every re-detection.
   Manifest shape (pinned contract): groups[{key, tier, members[{path, keeper?}]}].
   key is opaque group identity (exact: 16-hex sha256 prefix; probable: opaque
   composite string) — never re-derive it. Exactly one member per group carries
   keeper:true; unknown fields are ignored (tolerant reader).
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
    foreach ($e in $v.verdicts) { $existing[$e.key] = $e }
}
$out = @()
foreach ($g in $m.groups) {
    if ($existing.ContainsKey($g.key)) { $out += $existing[$g.key]; continue }
    $keep = $null
    if ($g.tier -eq 'exact') { $keep = ($g.members | Where-Object { $_.keeper -eq $true } | Select-Object -First 1).path }   # probable: keeper is a hint, no prefill
    $out += [pscustomobject]@{ key = $g.key; tier = $g.tier; verdict = $null; keepPath = $keep }
}
# keep curated history for keys no longer detected (paths may have moved)
foreach ($k in $existing.Keys) {
    if (-not ($m.groups | Where-Object { $_.key -eq $k })) { $out += $existing[$k] }
}
$doc = [pscustomobject]@{ version = 1; verdicts = $out }
$doc | ConvertTo-Json -Depth 5 | Set-Content -Path $Verdicts -Encoding UTF8
$counts = ($out | Group-Object { if ($null -eq $_.verdict) { 'uncurated' } else { $_.verdict } })
Write-Host ("init-dedupe-verdicts: entries={0} ({1})" -f $out.Count, (($counts | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' '))
