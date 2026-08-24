# End-to-end rig for the review ledger. Exit 1 on any failure.
#
# WHY THIS EXISTS, and why the unit asserts are not enough:
#
# The self-test pins every DECISION in the ledger — whether operator state survives an
# upsert, whether `auto` fires on silence, whether the envelope declares its kind. What it
# cannot see is the WIRING. If someone deleted a RecordReviewFailure call from a failure
# site, every one of those asserts would still pass and the ledger would quietly stop
# recording that class of failure. Same shape as a fake assert that pins a gate's behaviour
# while the bug is in the producer that never sets the gate's input.
#
# So this rig drives the REAL exe against real files on disk and asserts on the artefacts
# it produces. Fixtures are synthetic and generated here: a zero-byte file and a garbage
# file are enough to drive TagLib and the health gate into failure, so the rig needs no
# audio corpus, no network share, and nothing that can rot out from under it.
#
# Usage:
#   pwsh -File tools/tests/test-review-ledger.ps1
#   pwsh -File tools/tests/test-review-ledger.ps1 -TruedatExe path\to\truedat.exe

[CmdletBinding()]
param(
    [string] $TruedatExe
)

# Resolved in the body, not as a param default: $PSScriptRoot is not reliably bound when
# the default expression is evaluated, which made the no-argument invocation fail on
# Test-Path with an InvalidArgument rather than a readable message. A rig you cannot run
# without remembering a flag is a rig nobody runs.
if (-not $TruedatExe) {
    # tools\tests\this.ps1 -> tools\tests -> tools -> repo root
    $repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
    $TruedatExe = Join-Path $repoRoot 'dist\truedat\truedat.exe'
}

$ErrorActionPreference = 'Stop'
$script:failures = 0
function Assert($cond, $msg) {
    if ($cond) { Write-Host "PASS  $msg" }
    else { Write-Host "FAIL  $msg" -ForegroundColor Red; $script:failures++ }
}

# truedat writes progress and diagnostics to stderr as a matter of course, and under
# ErrorActionPreference=Stop a merged 2>&1 turns that into a terminating PS error. Drop
# to Continue for the duration of the call only, so genuine cmdlet failures in the rig
# still stop the run.
function Invoke-Truedat {
    param([string[]] $TruedatArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $TruedatExe @TruedatArgs 2>&1 | Out-String }
    finally { $ErrorActionPreference = $prev }
}

if (-not (Test-Path $TruedatExe)) {
    Write-Host "truedat.exe not found: $TruedatExe" -ForegroundColor Red
    Write-Host "Pass -TruedatExe <path>, or build dist first."
    exit 1
}
$TruedatExe = (Resolve-Path $TruedatExe).Path

$work = Join-Path $env:TEMP ("review-ledger-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$lib = Join-Path $work 'lib'
New-Item -ItemType Directory -Path $lib | Out-Null

try {
    # ── Fixtures ──────────────────────────────────────────────────────────────
    # Zero-byte and garbage files fail for DIFFERENT reasons (TagLib refuses to parse
    # one, the extractor refuses the other), which is what makes them a useful pair:
    # a rig that only produced one failure class could not tell a wired failure path
    # from a wired-once-and-copied one.
    $empty   = Join-Path $lib 'empty.mp3'
    $garbage = Join-Path $lib 'garbage.flac'
    $missing = Join-Path $lib 'never-existed.mp3'
    New-Item -ItemType File -Path $empty | Out-Null
    [IO.File]::WriteAllBytes($garbage, [byte[]](1..2048 | ForEach-Object { $_ % 251 }))

    $fileList = Join-Path $work 'files.txt'
    Set-Content -Path $fileList -Value @($empty, $garbage, $missing) -Encoding UTF8

    $moods  = Join-Path $lib 'mbxmoods.json'
    $ledger = Join-Path $lib 'mbxmoods-review.json'

    # ── Run 1: the failures must be RECORDED ─────────────────────────────────
    Invoke-Truedat @("--file-list", $fileList, "--moods", $moods) | Out-Null

    Assert (Test-Path $ledger) 'run 1: the ledger file is written'
    if (-not (Test-Path $ledger)) { throw 'no ledger — the rest of the rig cannot run' }

    $j = Get-Content $ledger -Raw | ConvertFrom-Json
    Assert ($j.kind -eq 'review') 'envelope: kind is DECLARED (the hub must not sniff the shape)'
    Assert ($null -ne $j.counts)  'envelope: carries a counts block'
    Assert ($j.records.Count -ge 2) "run 1: at least the two bad files are recorded (got $($j.records.Count))"

    $emptyRec = $j.records | Where-Object { $_.path -eq $empty }
    Assert ($null -ne $emptyRec) 'run 1: the zero-byte file has a record'
    if ($emptyRec) {
        Assert ($emptyRec.reason -and $emptyRec.reason.Length -gt 0) 'run 1: the record carries a reason'
        Assert ($emptyRec.firstSeen -and $emptyRec.lastSeen) 'run 1: the record is timestamped'
        Assert ($emptyRec.attempts -ge 1) 'run 1: the record counts an attempt'
    }

    # A path that never existed is a SKIP, not a failure — different disposition, and
    # conflating them is how a missing share reads as a corrupt library.
    $missRec = $j.records | Where-Object { $_.path -eq $missing }
    if ($missRec) {
        Assert ($missRec.disposition -eq 'skipped') 'run 1: a missing file records as skipped, not failed'
    }

    # ── Run 2: recorded files must NOT be re-attempted ───────────────────────
    $out2 = Invoke-Truedat @("--file-list", $fileList, "--moods", $moods)
    Assert ($out2 -match 'skipped review') 'run 2: a recorded file is skipped by the review rule'
    Assert ($out2 -notmatch '\[FAIL\]') 'run 2: nothing is re-attempted, so nothing fails again'

    $j2 = Get-Content $ledger -Raw | ConvertFrom-Json
    $emptyRec2 = $j2.records | Where-Object { $_.path -eq $empty }
    if ($emptyRec -and $emptyRec2) {
        Assert ($emptyRec2.firstSeen -eq $emptyRec.firstSeen) 'run 2: firstSeen is not rewritten — a rescan does not rewrite history'
        Assert ($emptyRec2.attempts -eq $emptyRec.attempts) 'run 2: an ordinary pass does not inflate the attempt count'
    }

    # ── Run 3: the OPERATOR'S state must survive a scan ──────────────────────
    # This is the property the whole design rests on, and the one the old error CSV
    # failed: it was deleted wholesale by the very command the advisor recommended,
    # so a decision never survived a retry.
    $j3 = Get-Content $ledger -Raw | ConvertFrom-Json
    foreach ($r in $j3.records) { if ($r.path -eq $empty) { $r.state = 'ignore' } }
    $j3 | ConvertTo-Json -Depth 12 | Set-Content -Path $ledger -Encoding UTF8

    Invoke-Truedat @("--file-list", $fileList, "--moods", $moods) | Out-Null
    $j4 = Get-Content $ledger -Raw | ConvertFrom-Json
    $emptyRec4 = $j4.records | Where-Object { $_.path -eq $empty }
    Assert ($emptyRec4 -and $emptyRec4.state -eq 'ignore') 'run 3: the OPERATOR state survives a scan (the core durability property)'

    # ── Run 4: --retry-errors must not override an auto conclusion ───────────
    # --retry-errors re-attempts `review` records ONLY. `ignore` and `auto` are both
    # decisions and both hold, or the operator's choice depends on which flag they last
    # typed. `empty` was set to ignore above, so it is the one to watch.
    $out4 = Invoke-Truedat @("--file-list", $fileList, "--moods", $moods, "--retry-errors")
    $j5 = Get-Content $ledger -Raw | ConvertFrom-Json
    $autoRec = $j5.records | Where-Object { $_.state -eq 'auto' } | Select-Object -First 1
    if ($autoRec) {
        Assert ($autoRec.stateReason -and $autoRec.stateReason.Length -gt 0) 'auto: an automatic conclusion always names its trigger'
    }
    Assert ($out4 -match 'ignored') '--retry-errors: an IGNORED record is still not re-attempted (a decision is a decision)'
    $emptyRec5 = $j5.records | Where-Object { $_.path -eq $empty }
    Assert ($emptyRec5 -and $emptyRec5.state -eq 'ignore') '--retry-errors: the ignore state survives the retry itself'

    # ── --list-review must NAME what --stats can only count ──────────────────
    $listed = Invoke-Truedat @("--list-review", $moods)
    Assert ($listed -match [regex]::Escape($empty)) '--list-review: NAMES the recorded file'
    Assert ($listed -match '=== Review ===') '--list-review: reports'
    # The advisor rule: never recommend a command that cannot clear the count.
    Assert ($listed -notmatch '--retry-errors') '--list-review: does not recommend a retry it cannot honour'
    Assert ($listed -notmatch '--refresh') '--list-review: does not recommend a refresh that cannot help'

    # ── A corrupt ledger must REFUSE, not silently start from empty ──────────
    Set-Content -Path $ledger -Value '{ this is not json' -Encoding UTF8
    $outBad = Invoke-Truedat @("--list-review", $moods)
    Assert ($outBad -match 'unreadable') 'corrupt ledger: refuses rather than reporting an empty review'
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:failures -gt 0) {
    Write-Host "$($script:failures) failure(s)" -ForegroundColor Red
    exit 1
}
Write-Host 'All review-ledger end-to-end checks passed.'
exit 0
