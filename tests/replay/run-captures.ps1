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
$exContract       = Join-Path $MbxRepoRoot "contracts\exclusions.contract.yaml"
$fixtures = Get-ChildItem "$PSScriptRoot\fixtures" -File | Sort-Object Name | Select-Object -ExpandProperty FullName

# --- prereqs (exit 2, not fake-green) ---
if (-not (Test-Path $TruedatExe))    { Fail2 "truedat.exe not found: $TruedatExe" }
if (-not (Test-Path $MbxCheckExe))   { Fail2 "mbxcheck.exe not found: $MbxCheckExe (build MbxCheck -c Release in the MBX repo)" }
if (-not (Test-Path $moodsContract)) { Fail2 "contract not found: $moodsContract" }
if (-not (Test-Path $identityContract)) { Fail2 "contract not found: $identityContract" }
if (-not (Test-Path $exContract))    { Fail2 "contract not found: $exContract (exclusion gates G1-G6)" }
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

    # --- exclusion gates G1-G6 (property assertions, invariant-style) ---------------
    # Each gate scans into its OWN fresh moods dir: exclusions never PRUNE existing
    # entries, so a shared moods would leave excluded tracks in place and false-red.
    # --exclusions is passed explicitly (mail setup note) so the gates are independent
    # of default resolution beside the moods file. mbxmoods.json is the fixed base name
    # in every gate dir, so the skipped sidecar is unambiguously mbxmoods-skipped.csv.
    Write-Host "replay: exclusion gates G1-G6..."

    # Build a proper JSON array by hand: ConvertTo-Json in PS 5.1 collapses a single-
    # element array to an object, which would break `rules`/`add`/`remove`.
    function RuleJson($h) { $h | ConvertTo-Json -Depth 4 -Compress }
    function Write-ExFile($path, $ruleObjs) {
        $rulesJson = (@($ruleObjs) | ForEach-Object { RuleJson $_ }) -join ",`n    "
        $u = (Get-Date).ToUniversalTime().ToString("o")
        $json = @"
{
  "schemaVersion": 1,
  "updatedUtc": "$u",
  "updatedBy": "replay",
  "rules": [
    $rulesJson
  ]
}
"@
        Set-Content -Encoding UTF8 -Path $path -Value $json
    }
    function Write-DeltaFile($path, $addObjs, $removeObjs) {
        $addJson = (@($addObjs)    | ForEach-Object { RuleJson $_ }) -join ",`n    "
        $remJson = (@($removeObjs) | ForEach-Object { RuleJson $_ }) -join ",`n    "
        $json = @"
{
  "schemaVersion": 1,
  "kind": "exclusion-decisions",
  "add": [
    $addJson
  ],
  "remove": [
    $remJson
  ]
}
"@
        Set-Content -Encoding UTF8 -Path $path -Value $json
    }
    function MoodsTrackKeys($moodsPath) {
        if (-not (Test-Path $moodsPath)) { return @() }
        $j = Get-Content $moodsPath -Raw | ConvertFrom-Json
        if ($null -eq $j.tracks) { return @() }
        return @($j.tracks.PSObject.Properties.Name)
    }
    function LeafInMoods($moodsPath, $leaf) {
        foreach ($k in (MoodsTrackKeys $moodsPath)) {
            if ($k.EndsWith($leaf, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
        }
        return $false
    }
    $exGatePass = 0; $exGateFail = 0; $exFails = @()
    function ExGate($name, $ok, $why) {
        if ($ok) { $script:exGatePass++; Write-Host "replay:   $name OK" }
        else { $script:exGateFail++; $script:exFails += ("{0}: {1}" -f $name, $why); Write-Host "replay:   $name FAILED - $why" }
    }

    $f0     = $fixtures[0]
    $f0leaf = Split-Path $f0 -Leaf

    # G1 EXCLUSION HONOURED: file rule excludes one fixture -> absent from moods,
    # present in skipped csv with reason 'excluded (rule: file=<path>)'. The >=1
    # scanned check proves the run really analyzed (not a trivially-empty pass).
    $g1 = Join-Path $work "g1"; New-Item -ItemType Directory -Path $g1 | Out-Null
    $g1moods = Join-Path $g1 "mbxmoods.json"; $g1ex = Join-Path $g1 "mbxmoods-exclude.json"
    Write-ExFile $g1ex @(@{ kind = "file"; action = "exclude"; path = $f0 })
    & $TruedatExe --file-list $list --moods $g1moods --exclusions $g1ex -p 2
    $g1exit = $LASTEXITCODE
    $g1absent  = -not (LeafInMoods $g1moods $f0leaf)
    $g1scanned = ((MoodsTrackKeys $g1moods).Count -ge 1)
    $g1skip = Join-Path $g1 "mbxmoods-skipped.csv"; $g1reason = $false
    if (Test-Path $g1skip) {
        $csv = Get-Content $g1skip -Raw
        $g1reason = ($csv -match [regex]::Escape($f0)) -and ($csv -match 'rule:\s*file=')
    }
    ExGate "G1 exclusion-honoured" (($g1exit -eq 0) -and $g1absent -and $g1scanned -and $g1reason) `
        "exit=$g1exit absent=$g1absent scanned>=1=$g1scanned skipReason=$g1reason"

    # G2 INCLUDE RESCUES: exclude + include the same path -> include wins -> analyzed.
    $g2 = Join-Path $work "g2"; New-Item -ItemType Directory -Path $g2 | Out-Null
    $g2moods = Join-Path $g2 "mbxmoods.json"; $g2ex = Join-Path $g2 "mbxmoods-exclude.json"
    Write-ExFile $g2ex @(
        @{ kind = "file"; action = "exclude"; path = $f0 },
        @{ kind = "file"; action = "include"; path = $f0 }
    )
    & $TruedatExe --file-list $list --moods $g2moods --exclusions $g2ex -p 2
    $g2exit = $LASTEXITCODE
    $g2present = LeafInMoods $g2moods $f0leaf
    ExGate "G2 include-rescues" (($g2exit -eq 0) -and $g2present) "exit=$g2exit present=$g2present"

    # G3 STRUCTURAL BEATS INCLUDE: an include folder rule cannot rescue a file the
    # --file-list path structurally cannot analyze. The structural skip is DSD by
    # extension (.dsf/.dff, UnsupportedExtensions): Program.cs:2340 skips it and RETURNS
    # before the include/exclude check at 2361, so include is never consulted. It counts
    # as a skip (flDsdSkipped), not a failure, so the scan exits 0 (2651). A zero-byte
    # .dsf suffices — skipped by extension, never read. NB a non-DSD unsupported ext like
    # .mp4 is NOT structurally skipped on --file-list (the video filter is MoodsMode-only)
    # — it reaches Essentia and FAILS (flFailed -> exit 1), which is why .dsf is used
    # here. --max-duration is likewise MoodsMode-only (3449) and unusable here. A real
    # fixture copied alongside (both under the include rule) proves the scan ran.
    $g3 = Join-Path $work "g3"; New-Item -ItemType Directory -Path $g3 | Out-Null
    $g3moods = Join-Path $g3 "mbxmoods.json"; $g3ex = Join-Path $g3 "mbxmoods-exclude.json"
    $g3real = Join-Path $g3 $f0leaf; Copy-Item $f0 $g3real
    $g3dsd  = Join-Path $g3 "structural-skip.dsf"; New-Item -ItemType File -Path $g3dsd | Out-Null
    $g3list = Join-Path $g3 "files.txt"; @($g3real, $g3dsd) | Set-Content -Encoding UTF8 $g3list
    Write-ExFile $g3ex @(@{ kind = "folder"; action = "include"; pattern = (Join-Path $g3 "**") })
    & $TruedatExe --file-list $g3list --moods $g3moods --exclusions $g3ex -p 2
    $g3exit = $LASTEXITCODE
    $g3realIn = LeafInMoods $g3moods $f0leaf
    $g3dsdIn  = LeafInMoods $g3moods "structural-skip.dsf"
    ExGate "G3 structural-beats-include" (($g3exit -eq 0) -and $g3realIn -and (-not $g3dsdIn)) `
        "exit=$g3exit realAnalyzed=$g3realIn dsdAnalyzed=$g3dsdIn (dsd must be false)"

    # G4 EXCLUSION FILE SHAPE: produce the canonical file via the WRITER (--apply-
    # exclusions), then validate it against the exclusions contract. Covers the writer.
    $g4 = Join-Path $work "g4"; New-Item -ItemType Directory -Path $g4 | Out-Null
    $g4ex = Join-Path $g4 "mbxmoods-exclude.json"; $g4delta = Join-Path $g4 "delta.json"
    Write-DeltaFile $g4delta @(@{ kind = "file"; action = "exclude"; path = $f0; note = "replay G4" }) @()
    & $TruedatExe --apply-exclusions $g4delta --exclusions $g4ex
    $g4apply = $LASTEXITCODE
    $g4wrote = Test-Path $g4ex
    $g4green = $false
    if ($g4wrote) {
        $g4rep = Join-Path $g4 "check.json"
        & $MbxCheckExe $g4ex --contract $exContract --report json --output $g4rep
        if ($LASTEXITCODE -eq 2) { Fail2 "mbxcheck config error on exclusions contract" }
        if (Test-Path $g4rep) {
            $g4sum = (Get-Content $g4rep -Raw | ConvertFrom-Json).summary
            $g4green = (($g4sum.failed -eq 0) -and ($g4sum.total -ge 1))
        }
    }
    ExGate "G4 exclusion-file-shape" (($g4apply -eq 0) -and $g4wrote -and $g4green) `
        "apply=$g4apply wrote=$g4wrote contractGreen=$g4green"

    # G5 MERGE IDEMPOTENCY: apply the same delta twice -> file byte-identical apart from
    # updatedUtc. Neutralize the updatedUtc value in both, then compare.
    $g5 = Join-Path $work "g5"; New-Item -ItemType Directory -Path $g5 | Out-Null
    $g5ex = Join-Path $g5 "mbxmoods-exclude.json"; $g5delta = Join-Path $g5 "delta.json"
    Write-DeltaFile $g5delta @(@{ kind = "genre"; action = "exclude"; value = "Podcast"; note = "replay G5" }) @()
    & $TruedatExe --apply-exclusions $g5delta --exclusions $g5ex
    $g5apply1 = $LASTEXITCODE
    $g5first  = if (Test-Path $g5ex) { Get-Content $g5ex -Raw } else { "" }
    & $TruedatExe --apply-exclusions $g5delta --exclusions $g5ex
    $g5apply2 = $LASTEXITCODE
    $g5second = if (Test-Path $g5ex) { Get-Content $g5ex -Raw } else { "" }
    $g5n1 = [regex]::Replace($g5first,  '("updatedUtc"\s*:\s*)"[^"]*"', '$1"X"')
    $g5n2 = [regex]::Replace($g5second, '("updatedUtc"\s*:\s*)"[^"]*"', '$1"X"')
    $g5idem = ($g5first -ne "") -and ($g5n1 -eq $g5n2)
    ExGate "G5 merge-idempotency" (($g5apply1 -eq 0) -and ($g5apply2 -eq 0) -and $g5idem) `
        "apply1=$g5apply1 apply2=$g5apply2 identicalModuloUpdatedUtc=$g5idem"

    # G6 BROKEN FILE REFUSES (the load-bearing gate): unparseable exclusions -> exit 1
    # AND no mbxmoods.json written. The whole design rests on refusing to scan rather
    # than silently analyzing everything the operator believes is excluded.
    $g6 = Join-Path $work "g6"; New-Item -ItemType Directory -Path $g6 | Out-Null
    $g6moods = Join-Path $g6 "mbxmoods.json"; $g6ex = Join-Path $g6 "mbxmoods-exclude.json"
    Set-Content -Encoding UTF8 -Path $g6ex -Value '{ this is not valid json ]['
    & $TruedatExe --file-list $list --moods $g6moods --exclusions $g6ex -p 2
    $g6exit = $LASTEXITCODE
    $g6noMoods = -not (Test-Path $g6moods)
    ExGate "G6 broken-file-refuses" (($g6exit -eq 1) -and $g6noMoods) `
        "exit=$g6exit (expected 1) wroteNoMoods=$g6noMoods"

    Write-Host "replay: exclusion gates passed=$exGatePass failed=$exGateFail"

    $total  = $s1.total + $s2.total + 1 + $exGatePass + $exGateFail
    $failed = $s1.failed + $s2.failed + $(if ($detPass) { 0 } else { 1 }) + $exGateFail
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
        foreach ($ef in $exFails) { Write-Host ("  FAIL {0}" -f $ef) }
        exit 1
    }
    exit 0
}
finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
