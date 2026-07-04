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

# ── Task 3: apply + idempotency + expected-under-Apply + skip-single-copy ──
# Extend manifest with h-exp (expected, untouched) and h-solo (skip-single-copy)
$manifest3 = @{ groups = @(
    @{ hash='h-exact'; tier='exact'; keeper='R:\lib\a\t.flac'; paths=@('R:\lib\a\t.flac','R:\lib\dupe\t.flac') },
    @{ hash='h-new';   tier='exact'; keeper='R:\lib\c\n.flac'; paths=@('R:\lib\c\n.flac','R:\lib\d\n.flac') },
    @{ hash='h-exp';   tier='exact'; keeper='R:\lib\e\x.flac'; paths=@('R:\lib\e\x.flac','R:\lib\f\x.flac') },
    @{ hash='h-solo';  tier='exact'; keeper='R:\lib\g\solo.flac'; paths=@('R:\lib\g\solo.flac','R:\lib\h\solo.flac') }
) } | ConvertTo-Json -Depth 5
Set-Content -Path $mPath -Value $manifest3 -Encoding UTF8

# Extend sidecar with h-exp (expected, should NOT touch) and h-solo (resolve, skip-single-copy)
$v4 = Get-Content $vPath -Raw | ConvertFrom-Json
foreach ($e in $v4.verdicts) {
    if ($e.hash -eq 'h-exact') { $e.verdict = 'resolve'; $e.keepPath = 'R:\lib\a\t.flac' }
    if ($e.hash -eq 'h-new')   { $e.verdict = 'resolve'; $e.keepPath = 'R:\lib\c\MISSING.flac' }
}
$v4.verdicts += @{ hash='h-exp'; tier='exact'; verdict='expected'; keepPath='R:\lib\e\x.flac' }
$v4.verdicts += @{ hash='h-solo'; tier='exact'; verdict='resolve'; keepPath='R:\lib\g\solo.flac' }
$v4 | ConvertTo-Json -Depth 5 | Set-Content -Path $vPath -Encoding UTF8

# Create files for h-exp (both copies)
foreach ($rel in 'e\x.flac','f\x.flac') {
    $p = Join-Path $root $rel
    New-Item -ItemType Directory -Path (Split-Path $p) -Force | Out-Null
    Set-Content -Path $p -Value 'x' -Encoding ASCII
}

# Create file for h-solo (only one copy to trigger skip-single-copy)
$p = Join-Path $root 'g\solo.flac'
New-Item -ItemType Directory -Path (Split-Path $p) -Force | Out-Null
Set-Content -Path $p -Value 'x' -Encoding ASCII

# First apply with -Apply
$log = Join-Path $work 'moves.csv'
$plan1 = & (Join-Path $tools 'apply-dedupe-verdicts.ps1') -Manifest $mPath -Verdicts $vPath `
    -ManifestRoot 'R:\lib' -Root $root -Apply -LogCsv $log
Assert (-not (Test-Path (Join-Path $root 'dupe\t.flac'))) 'apply: loser moved out of tree'
Assert (Test-Path (Join-Path ($root + '-quarantine') 'dupe\t.flac')) 'apply: loser in mirrored quarantine path'
Assert (Test-Path (Join-Path $root 'a\t.flac')) 'apply: keeper untouched'
Assert (Test-Path (Join-Path $root 'b\t.mp3')) 'apply: uncurated (probable) group untouched'
Assert (Test-Path $log) 'apply: move log written'
Assert (Test-Path (Join-Path $root 'e\x.flac')) 'apply: expected-verdict group file 1 untouched'
Assert (Test-Path (Join-Path $root 'f\x.flac')) 'apply: expected-verdict group file 2 untouched'
Assert (Test-Path (Join-Path $root 'g\solo.flac')) 'apply: skip-single-copy file untouched'
$soloEntry = $plan1 | Where-Object { $_.hash -eq 'h-solo' }
Assert ($null -ne $soloEntry -and $soloEntry.action -eq 'skip-single-copy') 'apply: h-solo plan action asserted as skip-single-copy'

# Second apply (idempotency)
$re = & (Join-Path $tools 'apply-dedupe-verdicts.ps1') -Manifest $mPath -Verdicts $vPath `
    -ManifestRoot 'R:\lib' -Root $root -Apply -LogCsv $log
Assert (@($re | Where-Object { $_.action -eq 'move' }).Count -eq 0) 'idempotent: second apply plans zero moves'
Assert (@($re | Where-Object { $_.action -eq 'skip-already-moved' }).Count -ge 1) 'idempotent: quarantined loser reported as already-moved'

# ── Task 3 (regression, Finding 1): multi-loser group survives partial quarantine ──
# h-multi: keeper + 2 losers, both losers under $root. First apply quarantines
# both. Then loser2 is restored to $root (a re-scan turning the file up again)
# while loser1 stays quarantined - this is exactly the state that tripped the
# old group-level short-circuit (any loser in quarantine => continue), which
# silently left loser2 live in the tree. Per-path classification must still
# plan and execute the move for loser2.
$manifestMulti = @{ groups = @(
    @{ hash='h-exact'; tier='exact'; keeper='R:\lib\a\t.flac'; paths=@('R:\lib\a\t.flac','R:\lib\dupe\t.flac') },
    @{ hash='h-new';   tier='exact'; keeper='R:\lib\c\n.flac'; paths=@('R:\lib\c\n.flac','R:\lib\d\n.flac') },
    @{ hash='h-exp';   tier='exact'; keeper='R:\lib\e\x.flac'; paths=@('R:\lib\e\x.flac','R:\lib\f\x.flac') },
    @{ hash='h-solo';  tier='exact'; keeper='R:\lib\g\solo.flac'; paths=@('R:\lib\g\solo.flac','R:\lib\h\solo.flac') },
    @{ hash='h-multi'; tier='exact'; keeper='R:\lib\i\keep.flac'; paths=@('R:\lib\i\keep.flac','R:\lib\j\loser1.flac','R:\lib\k\loser2.flac') }
) } | ConvertTo-Json -Depth 5
Set-Content -Path $mPath -Value $manifestMulti -Encoding UTF8

$v5 = Get-Content $vPath -Raw | ConvertFrom-Json
$v5.verdicts += @{ hash='h-multi'; tier='exact'; verdict='resolve'; keepPath='R:\lib\i\keep.flac' }
$v5 | ConvertTo-Json -Depth 5 | Set-Content -Path $vPath -Encoding UTF8

foreach ($rel in 'i\keep.flac','j\loser1.flac','k\loser2.flac') {
    $p = Join-Path $root $rel
    New-Item -ItemType Directory -Path (Split-Path $p) -Force | Out-Null
    Set-Content -Path $p -Value 'x' -Encoding ASCII
}

& (Join-Path $tools 'apply-dedupe-verdicts.ps1') -Manifest $mPath -Verdicts $vPath `
    -ManifestRoot 'R:\lib' -Root $root -Apply -LogCsv $log | Out-Null
Assert (-not (Test-Path (Join-Path $root 'j\loser1.flac'))) 'multi-loser: loser1 quarantined on first apply'
Assert (-not (Test-Path (Join-Path $root 'k\loser2.flac'))) 'multi-loser: loser2 quarantined on first apply'

# Restore loser2 into root; loser1 stays quarantined - the bug scenario.
$restoredPath = Join-Path $root 'k\loser2.flac'
Set-Content -Path $restoredPath -Value 'x' -Encoding ASCII
Assert (Test-Path $restoredPath) 'multi-loser: loser2 restored to root before regression apply'

$re2 = & (Join-Path $tools 'apply-dedupe-verdicts.ps1') -Manifest $mPath -Verdicts $vPath `
    -ManifestRoot 'R:\lib' -Root $root -Apply -LogCsv $log
$multiEntries = @($re2 | Where-Object { $_.hash -eq 'h-multi' })
Assert ((@($multiEntries | Where-Object { $_.action -eq 'move' -and $_.from -eq $restoredPath }).Count) -eq 1) 'multi-loser: loser2 planned as move (partial quarantine does not mask it)'
Assert (-not (Test-Path $restoredPath)) 'multi-loser: loser2 gone from root after regression apply'
Assert ((@($multiEntries | Where-Object { $_.action -eq 'skip-already-moved' -and $_.from -eq (Join-Path $root 'j\loser1.flac') }).Count) -eq 1) 'multi-loser: loser1 still reported skip-already-moved'
Assert (Test-Path (Join-Path $root 'i\keep.flac')) 'multi-loser: keeper untouched'

# ── Task 4: replay against a second root (prod simulation) ──
$prod = Join-Path $work 'prodlib'
foreach ($rel in 'a\t.flac','dupe\t.flac') {
    $p = Join-Path $prod $rel
    New-Item -ItemType Directory -Path (Split-Path $p) -Force | Out-Null
    Set-Content -Path $p -Value 'x' -Encoding ASCII
}
& (Join-Path $tools 'apply-dedupe-verdicts.ps1') -Manifest $mPath -Verdicts $vPath `
    -ManifestRoot 'R:\lib' -Root $prod -Apply | Out-Null
Assert (-not (Test-Path (Join-Path $prod 'dupe\t.flac'))) 'replay: same sidecar resolves the loser under the prod root'
Assert (Test-Path (Join-Path $prod 'a\t.flac')) 'replay: prod keeper untouched'
Assert (Test-Path (Join-Path ($prod + '-quarantine') 'dupe\t.flac')) 'replay: prod quarantine mirrored'

Write-Host ''
if ($script:failures -gt 0) { Write-Host "$($script:failures) FAILURES" -ForegroundColor Red; exit 1 }
Write-Host 'ALL PASS'; exit 0
