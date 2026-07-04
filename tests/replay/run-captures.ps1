# Contract replay for truedat: scan committed fixtures, validate outputs against
# the MBX contracts, verify identity determinism. Invoked by huddle 'replay truedat'.
param(
    [Parameter(Mandatory)][string]$OutputSummary,
    [string]$TruedatExe   = "",
    [string]$MbxRepoRoot  = "C:\Users\scott\source\repos\MBX",
    [string]$MbxCheckExe  = ""
)
$ErrorActionPreference = "Stop"
# Write-Error inside a function invoked from a pipeline/try-block can be swallowed under
# -ErrorActionPreference Stop; write straight to stderr and exit so the prereq message
# reliably reaches the console instead of a fake-green run.
function Fail2($msg) { [Console]::Error.WriteLine($msg); exit 2 }

# Windows PowerShell 5.1 gotcha: under `-File` invocation, $PSScriptRoot is empty while
# parameter DEFAULT-VALUE expressions are evaluated if the param block also contains a
# [Parameter(Mandatory)] parameter (confirmed: reproduces regardless of parameter order,
# and regardless of which mandatory param it is). Resolving $PSScriptRoot-based defaults
# in the body (where it's correctly populated) avoids silently building a broken relative
# path like "\..\..\dist\truedat\truedat.exe" that then fails prereq checks.
if ($TruedatExe -eq "") { $TruedatExe = Join-Path $PSScriptRoot "..\..\dist\truedat\truedat.exe" }
if ($MbxCheckExe -eq "") { $MbxCheckExe = Join-Path $MbxRepoRoot "MbxCheck\bin\Release\net8.0\mbxcheck.exe" }
$moodsContract    = Join-Path $MbxRepoRoot "contracts\mbxmoods.contract.yaml"
$identityContract = Join-Path $MbxRepoRoot "contracts\identity.contract.yaml"
$fixtures = Get-ChildItem "$PSScriptRoot\fixtures" -File | Sort-Object Name | Select-Object -ExpandProperty FullName

# --- prereqs (exit 2, not fake-green) ---
if (-not (Test-Path $TruedatExe))    { Fail2 "truedat.exe not found: $TruedatExe" }
if (-not (Test-Path $MbxCheckExe))   { Fail2 "mbxcheck.exe not found: $MbxCheckExe (build MbxCheck -c Release in the MBX repo)" }
if (-not (Test-Path $moodsContract)) { Fail2 "contract not found: $moodsContract" }
if (-not (Test-Path $identityContract)) { Fail2 "contract not found: $identityContract" }
if ($fixtures.Count -lt 3)           { Fail2 "fixtures missing under tests\replay\fixtures (run make-fixtures.ps1)" }
$essentia = Join-Path (Split-Path $TruedatExe) "essentia_streaming_extractor_music.exe"
if (-not (Test-Path $essentia))      { Fail2 "essentia_streaming_extractor_music.exe not beside truedat.exe" }

$work = Join-Path $env:TEMP ("truedat-replay-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $list = Join-Path $work "files.txt"
    $fixtures | Set-Content -Encoding UTF8 $list

    Write-Host "replay: scanning fixtures (analysis codepaths)..."
    $moods = Join-Path $work "mbxmoods.json"
    & $TruedatExe --file-list $list --moods $moods -p 2
    if ($LASTEXITCODE -ne 0) { Fail2 "truedat scan failed (exit $LASTEXITCODE)" }
    if (-not (Test-Path $moods)) { Fail2 "scan produced no mbxmoods.json" }

    Write-Host "replay: identity pass x2 (determinism)..."
    $m1 = Join-Path $work "id1.ndjson"; $m2 = Join-Path $work "id2.ndjson"
    & $TruedatExe --hash-only --level stream --file-list $list --output $m1
    if ($LASTEXITCODE -ne 0) { Fail2 "hash-only pass 1 failed" }
    & $TruedatExe --hash-only --level stream --file-list $list --output $m2
    if ($LASTEXITCODE -ne 0) { Fail2 "hash-only pass 2 failed" }

    # determinism: audioStreamSha256 per path identical across runs, all non-empty
    $sha = @{}; $detPass = $true; $detWhy = ""
    foreach ($line in Get-Content $m1) { $o = $line | ConvertFrom-Json; $sha[$o.path] = $o.identity.audioStreamSha256 }
    $run2Count = 0
    foreach ($line in Get-Content $m2) {
        $o = $line | ConvertFrom-Json; $run2Count++
        if (-not $sha.ContainsKey($o.path)) { $detPass = $false; $detWhy = "path set differs: $($o.path)"; break }
        if ([string]::IsNullOrEmpty($o.identity.audioStreamSha256) -or $sha[$o.path] -ne $o.identity.audioStreamSha256) {
            $detPass = $false; $detWhy = "sha mismatch for $($o.path)"; break
        }
    }
    # run2 subset-of run1 is checked above; count equality closes the reverse gap
    # (a path in run1 that run2 dropped).
    if ($detPass -and $run2Count -ne $sha.Count) { $detPass = $false; $detWhy = "manifest counts differ: run1=$($sha.Count) run2=$run2Count" }
    if (-not $detPass) { Write-Host "replay: DETERMINISM FAILED - $detWhy" } else { Write-Host "replay: determinism OK ($($sha.Count) files)" }

    Write-Host "replay: contract checks..."
    $r1 = Join-Path $work "check-moods.json"; $r2 = Join-Path $work "check-identity.json"
    & $MbxCheckExe $moods --contract $moodsContract --each tracks --report json --output $r1
    $moodsExit = $LASTEXITCODE
    & $MbxCheckExe $m1 --contract $identityContract --report json --output $r2
    $idExit = $LASTEXITCODE
    if ($moodsExit -eq 2 -or $idExit -eq 2) { Fail2 "mbxcheck config error" }
    if (-not (Test-Path $r1) -or -not (Test-Path $r2)) { Fail2 "mbxcheck produced no report" }

    $s1 = (Get-Content $r1 -Raw | ConvertFrom-Json).summary
    $s2 = (Get-Content $r2 -Raw | ConvertFrom-Json).summary
    $total  = $s1.total + $s2.total + 1
    $failed = $s1.failed + $s2.failed + $(if ($detPass) { 0 } else { 1 })
    $passed = $total - $failed
    @{ summary = @{ total = $total; passed = $passed; failed = $failed; skipped = 0 } } |
        ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $OutputSummary
    Write-Host "replay: total=$total passed=$passed failed=$failed"
    if ($failed -gt 0) {
        # surface failing gate names for the console log
        foreach ($rep in @($r1, $r2)) {
            (Get-Content $rep -Raw | ConvertFrom-Json).results | Where-Object { -not $_.passed } |
                ForEach-Object { Write-Host ("  FAIL {0} ({1}): {2}" -f $_.gate, $_.op, ($_.failedDocuments -join "; ")) }
        }
        exit 1
    }
    exit 0
}
finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
