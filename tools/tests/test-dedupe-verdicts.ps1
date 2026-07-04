# Plain-PS test harness for the dedupe curation scripts. Exit 1 on any failure.
# Manifest fixtures below conform to the pinned mbxmoods-duplicates.json contract:
# groups[{key, tier, scope, members[{path, keeper?}]}] with member fields
# omit-when-missing. key is opaque group identity (exact: 16-hex sha256 prefix;
# probable: opaque composite string) - never re-derived by the scripts under test.
$ErrorActionPreference = 'Stop'
$script:failures = 0
function Assert($cond, $msg) {
    if ($cond) { Write-Host "PASS  $msg" }
    else { Write-Host "FAIL  $msg" -ForegroundColor Red; $script:failures++ }
}
$tools = Split-Path -Parent $PSScriptRoot   # tools/
$work = Join-Path $env:TEMP ("dedupe-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

# Realistic-looking opaque group keys (exact: 16-hex sha256 prefix; probable:
# opaque composite string - treated as opaque, never parsed by the scripts).
$keyExact    = 'aaaa1111bbbb2222'
$keyProb     = 'Artist B,Title T|mp3,flac'
$keyNew      = 'cccc3333dddd4444'
$keyExp      = 'eeee5555ffff6666'
$keySolo     = '1111aaaa2222bbbb'
$keyMulti    = '3333cccc4444dddd'
$keyConflict = '5555eeee6666ffff'
$keyBad      = '7777111188882222'

# ── Task 1: init + merge ──
# This fixture also carries the top-level version/generated/moodsFile/skipped
# fields (and extra per-member metadata) to prove tolerant reading: the scripts
# under test must ignore all of it and only look at key/tier/members[].path/keeper.
$manifest = @{
    version = 1
    generated = '2026-07-04T00:00:00Z'
    moodsFile = 'R:\lib\mbxmoods.json'
    skipped = @{ noHash = 0; noFeatures = 0 }
    groups = @(
        @{ id=1; key=$keyExact; tier='exact'; scope='same-folder'; members=@(
            @{ path='R:\lib\a\t.flac'; keeper=$true; artist='A'; title='T'; codec='flac' },
            @{ path='R:\lib\dupe\t.flac'; codec='flac' }
        ) },
        @{ id=2; key=$keyProb; tier='probable'; scope='cross-folder'; members=@(
            @{ path='R:\lib\b\t.mp3'; keeper=$true },
            @{ path='R:\lib\b\t.flac' }
        ) }
    )
} | ConvertTo-Json -Depth 8
$mPath = Join-Path $work 'mbxmoods-duplicates.json'
$vPath = Join-Path $work 'mbxmoods-duplicates.verdicts.json'
Set-Content -Path $mPath -Value $manifest -Encoding UTF8

& (Join-Path $tools 'init-dedupe-verdicts.ps1') -Manifest $mPath -Verdicts $vPath
$v = Get-Content $vPath -Raw | ConvertFrom-Json
Assert ($v.version -eq 1) 'init: sidecar version 1'
Assert ($v.verdicts.Count -eq 2) 'init: one entry per manifest group'
$ex = $v.verdicts | Where-Object { $_.key -eq $keyExact }
Assert ($null -eq $ex.verdict) 'init: exact starts uncurated'
Assert ($ex.keepPath -eq 'R:\lib\a\t.flac') 'init: exact keepPath prefilled from keeper member'
$pr = $v.verdicts | Where-Object { $_.key -eq $keyProb }
Assert ($null -eq $pr.keepPath) 'init: probable gets NO keepPath prefill (even though a keeper member is present)'

# merge: curate h-exact as expected, re-init with a new group added
$v.verdicts | Where-Object { $_.key -eq $keyExact } | ForEach-Object { $_.verdict = 'expected' }
$v | ConvertTo-Json -Depth 5 | Set-Content -Path $vPath -Encoding UTF8
$manifest2 = @{ groups = @(
    @{ id=1; key=$keyExact; tier='exact'; scope='same-folder'; members=@(
        @{ path='R:\lib\a\t.flac'; keeper=$true },
        @{ path='R:\lib\dupe\t.flac' }
    ) },
    @{ id=2; key=$keyNew; tier='exact'; scope='same-folder'; members=@(
        @{ path='R:\lib\c\n.flac'; keeper=$true },
        @{ path='R:\lib\d\n.flac' }
    ) }
) } | ConvertTo-Json -Depth 8
Set-Content -Path $mPath -Value $manifest2 -Encoding UTF8
& (Join-Path $tools 'init-dedupe-verdicts.ps1') -Manifest $mPath -Verdicts $vPath
$v2 = Get-Content $vPath -Raw | ConvertFrom-Json
Assert (($v2.verdicts | Where-Object { $_.key -eq $keyExact }).verdict -eq 'expected') 'merge: existing verdict preserved (suppression)'
Assert ((@($v2.verdicts | Where-Object { $_.key -eq $keyNew }).Count) -eq 1) 'merge: new group appended'
Assert ((@($v2.verdicts | Where-Object { $_.key -eq $keyProb }).Count) -eq 1) 'merge: vanished key kept as history'

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
    if ($e.key -eq $keyExact) { $e.verdict = 'resolve'; $e.keepPath = 'R:\lib\a\t.flac' }
    if ($e.key -eq $keyNew)   { $e.verdict = 'resolve'; $e.keepPath = 'R:\lib\c\MISSING.flac' }
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
    @{ id=1; key=$keyExact; tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\a\t.flac';keeper=$true},@{path='R:\lib\dupe\t.flac'}) },
    @{ id=2; key=$keyNew;   tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\c\n.flac';keeper=$true},@{path='R:\lib\d\n.flac'}) },
    @{ id=3; key=$keyExp;   tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\e\x.flac';keeper=$true},@{path='R:\lib\f\x.flac'}) },
    @{ id=4; key=$keySolo;  tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\g\solo.flac';keeper=$true},@{path='R:\lib\h\solo.flac'}) }
) } | ConvertTo-Json -Depth 8
Set-Content -Path $mPath -Value $manifest3 -Encoding UTF8

# Extend sidecar with h-exp (expected, should NOT touch) and h-solo (resolve, skip-single-copy)
$v4 = Get-Content $vPath -Raw | ConvertFrom-Json
foreach ($e in $v4.verdicts) {
    if ($e.key -eq $keyExact) { $e.verdict = 'resolve'; $e.keepPath = 'R:\lib\a\t.flac' }
    if ($e.key -eq $keyNew)   { $e.verdict = 'resolve'; $e.keepPath = 'R:\lib\c\MISSING.flac' }
}
$v4.verdicts += @{ key=$keyExp; tier='exact'; verdict='expected'; keepPath='R:\lib\e\x.flac' }
$v4.verdicts += @{ key=$keySolo; tier='exact'; verdict='resolve'; keepPath='R:\lib\g\solo.flac' }
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
$soloEntry = $plan1 | Where-Object { $_.key -eq $keySolo }
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
    @{ id=1; key=$keyExact; tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\a\t.flac';keeper=$true},@{path='R:\lib\dupe\t.flac'}) },
    @{ id=2; key=$keyNew;   tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\c\n.flac';keeper=$true},@{path='R:\lib\d\n.flac'}) },
    @{ id=3; key=$keyExp;   tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\e\x.flac';keeper=$true},@{path='R:\lib\f\x.flac'}) },
    @{ id=4; key=$keySolo;  tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\g\solo.flac';keeper=$true},@{path='R:\lib\h\solo.flac'}) },
    @{ id=5; key=$keyMulti; tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\i\keep.flac';keeper=$true},@{path='R:\lib\j\loser1.flac'},@{path='R:\lib\k\loser2.flac'}) }
) } | ConvertTo-Json -Depth 8
Set-Content -Path $mPath -Value $manifestMulti -Encoding UTF8

$v5 = Get-Content $vPath -Raw | ConvertFrom-Json
$v5.verdicts += @{ key=$keyMulti; tier='exact'; verdict='resolve'; keepPath='R:\lib\i\keep.flac' }
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
$multiEntries = @($re2 | Where-Object { $_.key -eq $keyMulti })
# Loser2 reappeared at a root path whose quarantine mirror is already occupied
# (from the first apply). Per-path classification still surfaces it (the
# original masking bug this test guards against - the group-level short
# circuit that silently skipped the whole group) but the N1 clobber guard
# means it is now a conflict, not a blind overwrite-move: the existing
# quarantine copy is never known to be identical, so it must not be clobbered.
Assert ((@($multiEntries | Where-Object { $_.action -eq 'conflict-dest-exists' -and $_.from -eq $restoredPath }).Count) -eq 1) 'multi-loser: loser2 surfaced as conflict-dest-exists (partial quarantine does not mask it, and the existing quarantine copy is not clobbered)'
Assert (Test-Path $restoredPath) 'multi-loser: loser2 left in root untouched (move refused rather than overwriting the existing quarantine copy)'
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

# ── Task 5 (final review): N1 quarantine-clobber guard, N2 dry-run LogCsv dir, E unknown verdict ──

# N1: conflict-dest-exists must never let -Force overwrite a quarantined file.
$manifest5 = @{ groups = @(
    @{ id=1; key=$keyExact;    tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\a\t.flac';keeper=$true},@{path='R:\lib\dupe\t.flac'}) },
    @{ id=2; key=$keyNew;      tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\c\n.flac';keeper=$true},@{path='R:\lib\d\n.flac'}) },
    @{ id=3; key=$keyExp;      tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\e\x.flac';keeper=$true},@{path='R:\lib\f\x.flac'}) },
    @{ id=4; key=$keySolo;     tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\g\solo.flac';keeper=$true},@{path='R:\lib\h\solo.flac'}) },
    @{ id=5; key=$keyMulti;    tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\i\keep.flac';keeper=$true},@{path='R:\lib\j\loser1.flac'},@{path='R:\lib\k\loser2.flac'}) },
    @{ id=6; key=$keyConflict; tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\m\keep.flac';keeper=$true},@{path='R:\lib\n\loser.flac'}) }
) } | ConvertTo-Json -Depth 8
Set-Content -Path $mPath -Value $manifest5 -Encoding UTF8

$v6 = Get-Content $vPath -Raw | ConvertFrom-Json
$v6.verdicts += @{ key=$keyConflict; tier='exact'; verdict='resolve'; keepPath='R:\lib\m\keep.flac' }
$v6 | ConvertTo-Json -Depth 5 | Set-Content -Path $vPath -Encoding UTF8

$keepConflictPath = Join-Path $root 'm\keep.flac'
New-Item -ItemType Directory -Path (Split-Path $keepConflictPath) -Force | Out-Null
Set-Content -Path $keepConflictPath -Value 'x' -Encoding ASCII

$loserPath = Join-Path $root 'n\loser.flac'
New-Item -ItemType Directory -Path (Split-Path $loserPath) -Force | Out-Null
$originalContent = 'original-content-h-conflict-12345'
Set-Content -Path $loserPath -Value $originalContent -Encoding ASCII -NoNewline

# First apply quarantines the loser normally (no conflict yet).
& (Join-Path $tools 'apply-dedupe-verdicts.ps1') -Manifest $mPath -Verdicts $vPath `
    -ManifestRoot 'R:\lib' -Root $root -Apply -LogCsv $log | Out-Null
$quarantinedPath = Join-Path ($root + '-quarantine') 'n\loser.flac'
Assert (Test-Path $quarantinedPath) 'conflict: loser quarantined on first apply'
Assert (-not (Test-Path $loserPath)) 'conflict: loser gone from root after first apply'

# Simulate the file reappearing at the same root path with DIFFERENT content
# (e.g. a re-scan turning up a new/different file at the same path) while the
# original loser is still sitting in quarantine from the first apply.
$differentContent = 'different-content-reappeared-67890'
Set-Content -Path $loserPath -Value $differentContent -Encoding ASCII -NoNewline

$re3 = & (Join-Path $tools 'apply-dedupe-verdicts.ps1') -Manifest $mPath -Verdicts $vPath `
    -ManifestRoot 'R:\lib' -Root $root -Apply -LogCsv $log
$conflictEntries = @($re3 | Where-Object { $_.key -eq $keyConflict -and $_.action -eq 'conflict-dest-exists' })
Assert ($conflictEntries.Count -eq 1) 'conflict: conflict-dest-exists plan entry emitted'
Assert (($conflictEntries[0].from -eq $loserPath) -and ($conflictEntries[0].to -eq $quarantinedPath)) 'conflict: plan entry from/to point at root path and quarantine path'
Assert (Test-Path $loserPath) 'conflict: root file NOT moved - still present'
Assert ((Get-Content -Path $loserPath -Raw) -eq $differentContent) 'conflict: root file content is the reappeared (different) content, untouched'
Assert ((Get-Content -Path $quarantinedPath -Raw) -eq $originalContent) 'conflict: quarantined file content UNCHANGED (not clobbered by -Force)'

# ── N2: dry-run with -LogCsv pointing into a not-yet-created directory must not throw ──
$newLogDir = Join-Path $work 'newlogs\nested'
$newLog = Join-Path $newLogDir 'plan.csv'
Assert (-not (Test-Path $newLogDir)) 'logcsv-dir: target directory does not exist before dry-run'
$dryPlan = & (Join-Path $tools 'apply-dedupe-verdicts.ps1') -Manifest $mPath -Verdicts $vPath `
    -ManifestRoot 'R:\lib' -Root $root -LogCsv $newLog
Assert ($null -ne $dryPlan -and @($dryPlan).Count -gt 0) 'logcsv-dir: dry-run with missing LogCsv parent returns plan objects (does not throw)'
Assert (Test-Path $newLog) 'logcsv-dir: CSV file created despite missing parent directory'

# ── E: unknown/typo verdict must be surfaced, never silently dropped ──
$manifest6 = @{ groups = @(
    @{ id=1; key=$keyExact;    tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\a\t.flac';keeper=$true},@{path='R:\lib\dupe\t.flac'}) },
    @{ id=2; key=$keyNew;      tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\c\n.flac';keeper=$true},@{path='R:\lib\d\n.flac'}) },
    @{ id=3; key=$keyExp;      tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\e\x.flac';keeper=$true},@{path='R:\lib\f\x.flac'}) },
    @{ id=4; key=$keySolo;     tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\g\solo.flac';keeper=$true},@{path='R:\lib\h\solo.flac'}) },
    @{ id=5; key=$keyMulti;    tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\i\keep.flac';keeper=$true},@{path='R:\lib\j\loser1.flac'},@{path='R:\lib\k\loser2.flac'}) },
    @{ id=6; key=$keyConflict; tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\m\keep.flac';keeper=$true},@{path='R:\lib\n\loser.flac'}) },
    @{ id=7; key=$keyBad;      tier='exact'; scope='same-folder'; members=@(@{path='R:\lib\p\keep.flac';keeper=$true},@{path='R:\lib\q\loser.flac'}) }
) } | ConvertTo-Json -Depth 8
Set-Content -Path $mPath -Value $manifest6 -Encoding UTF8

$v7 = Get-Content $vPath -Raw | ConvertFrom-Json
$v7.verdicts += @{ key=$keyBad; tier='exact'; verdict='reslove'; keepPath='R:\lib\p\keep.flac' }
$v7 | ConvertTo-Json -Depth 5 | Set-Content -Path $vPath -Encoding UTF8

# Redirect the warning stream (3) into the success stream so it can be
# inspected alongside the returned plan objects.
$mixed = & (Join-Path $tools 'apply-dedupe-verdicts.ps1') -Manifest $mPath -Verdicts $vPath `
    -ManifestRoot 'R:\lib' -Root $root 3>&1
$warnRecords = @($mixed | Where-Object { $_ -is [System.Management.Automation.WarningRecord] })
$planObjects = @($mixed | Where-Object { $_ -isnot [System.Management.Automation.WarningRecord] })
$unknownEntry = @($planObjects | Where-Object { $_.key -eq $keyBad -and $_.action -eq 'skip-unknown-verdict' })
Assert ($unknownEntry.Count -eq 1) 'unknown-verdict: skip-unknown-verdict plan entry emitted for bad verdict value'
Assert ($null -eq $unknownEntry[0].from -and $null -eq $unknownEntry[0].to) 'unknown-verdict: plan entry carries null from/to (no path to act on)'
Assert (($warnRecords.Count -ge 1) -and ($warnRecords[0].Message -like "*$keyBad*") -and ($warnRecords[0].Message -like '*reslove*')) 'unknown-verdict: warning names the offending key and the bad verdict value'

Write-Host ''
if ($script:failures -gt 0) { Write-Host "$($script:failures) FAILURES" -ForegroundColor Red; exit 1 }
Write-Host 'ALL PASS'; exit 0
