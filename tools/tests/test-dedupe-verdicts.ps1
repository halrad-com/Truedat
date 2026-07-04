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

# ── Task 2: apply dry-run ──
$root = Join-Path $work 'lib'
foreach ($rel in 'a\t.flac','dupe\t.flac','b\t.mp3','b\t.flac','c\n.flac') {
    $p = Join-Path $root $rel
    New-Item -ItemType Directory -Path (Split-Path $p) -Force | Out-Null
    Set-Content -Path $p -Value 'x' -Encoding ASCII
}
# curate: h-exact resolve (keep a\t.flac); h-prob stays uncurated; h-new resolve but keeper missing on disk
$v3 = Get-Content $vPath -Raw | ConvertFrom-Json
foreach ($e in $v3.verdicts) {
    if ($e.hash -eq 'h-exact') { $e.verdict = 'resolve'; $e.keepPath = 'R:\lib\a\t.flac' }
    if ($e.hash -eq 'h-new')   { $e.verdict = 'resolve'; $e.keepPath = 'R:\lib\c\MISSING.flac' }
}
$v3 | ConvertTo-Json -Depth 5 | Set-Content -Path $vPath -Encoding UTF8
Set-Content -Path $mPath -Value $manifest2 -Encoding UTF8

$plan = & (Join-Path $tools 'apply-dedupe-verdicts.ps1') -Manifest $mPath -Verdicts $vPath `
            -ManifestRoot 'R:\lib' -Root $root
$mv = @($plan | Where-Object { $_.action -eq 'move' })
Assert ($mv.Count -eq 1) 'dryrun: exactly one planned move (h-exact loser)'
Assert ($mv[0].from -eq (Join-Path $root 'dupe\t.flac')) 'dryrun: loser path re-rooted to target'
Assert ($mv[0].to -eq (Join-Path ($root + '-quarantine') 'dupe\t.flac')) 'dryrun: quarantine mirrors relpath'
Assert (@($plan | Where-Object { $_.action -eq 'skip-keeper-missing' }).Count -ge 1) 'dryrun: keeper missing on disk -> group skipped'
Assert ((Test-Path (Join-Path $root 'dupe\t.flac'))) 'dryrun: no file moved without -Apply'

Write-Host ''
if ($script:failures -gt 0) { Write-Host "$($script:failures) FAILURES" -ForegroundColor Red; exit 1 }
Write-Host 'ALL PASS'; exit 0
