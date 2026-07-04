# Plain-PS test harness for the dedupe curation scripts. Exit 1 on any failure.
$ErrorActionPreference = 'Stop'
$script:failures = 0
function Assert($cond, $msg) {
    if ($cond) { Write-Host "PASS  $msg" }
    else { Write-Host "FAIL  $msg" -ForegroundColor Red; $script:failures++ }
}
$tools = Split-Path -Parent $PSScriptRoot   # tools/
$work = Join-Path $env:TEMP ("dedupe-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

# ── Task 1: init + merge ──
$manifest = @{ groups = @(
    @{ hash='h-exact'; tier='exact';    keeper='R:\lib\a\t.flac'; paths=@('R:\lib\a\t.flac','R:\lib\dupe\t.flac') },
    @{ hash='h-prob';  tier='probable'; keeper='R:\lib\b\t.mp3';  paths=@('R:\lib\b\t.mp3','R:\lib\b\t.flac') }
) } | ConvertTo-Json -Depth 5
$mPath = Join-Path $work 'mbxmoods-duplicates.json'
$vPath = Join-Path $work 'mbxmoods-duplicates.verdicts.json'
Set-Content -Path $mPath -Value $manifest -Encoding UTF8

& (Join-Path $tools 'init-dedupe-verdicts.ps1') -Manifest $mPath -Verdicts $vPath
$v = Get-Content $vPath -Raw | ConvertFrom-Json
Assert ($v.version -eq 1) 'init: sidecar version 1'
Assert ($v.verdicts.Count -eq 2) 'init: one entry per manifest group'
$ex = $v.verdicts | Where-Object { $_.hash -eq 'h-exact' }
Assert ($null -eq $ex.verdict) 'init: exact starts uncurated'
Assert ($ex.keepPath -eq 'R:\lib\a\t.flac') 'init: exact keepPath prefilled from keeper'
$pr = $v.verdicts | Where-Object { $_.hash -eq 'h-prob' }
Assert ($null -eq $pr.keepPath) 'init: probable gets NO keepPath prefill'

# merge: curate h-exact as expected, re-init with a new group added
$v.verdicts | Where-Object { $_.hash -eq 'h-exact' } | ForEach-Object { $_.verdict = 'expected' }
$v | ConvertTo-Json -Depth 5 | Set-Content -Path $vPath -Encoding UTF8
$manifest2 = @{ groups = @(
    @{ hash='h-exact'; tier='exact'; keeper='R:\lib\a\t.flac'; paths=@('R:\lib\a\t.flac','R:\lib\dupe\t.flac') },
    @{ hash='h-new';   tier='exact'; keeper='R:\lib\c\n.flac'; paths=@('R:\lib\c\n.flac','R:\lib\d\n.flac') }
) } | ConvertTo-Json -Depth 5
Set-Content -Path $mPath -Value $manifest2 -Encoding UTF8
& (Join-Path $tools 'init-dedupe-verdicts.ps1') -Manifest $mPath -Verdicts $vPath
$v2 = Get-Content $vPath -Raw | ConvertFrom-Json
Assert (($v2.verdicts | Where-Object { $_.hash -eq 'h-exact' }).verdict -eq 'expected') 'merge: existing verdict preserved (suppression)'
Assert ((@($v2.verdicts | Where-Object { $_.hash -eq 'h-new' }).Count) -eq 1) 'merge: new group appended'
Assert ((@($v2.verdicts | Where-Object { $_.hash -eq 'h-prob' }).Count) -eq 1) 'merge: vanished hash kept as history'

Write-Host ''
if ($script:failures -gt 0) { Write-Host "$($script:failures) FAILURES" -ForegroundColor Red; exit 1 }
Write-Host 'ALL PASS'; exit 0
