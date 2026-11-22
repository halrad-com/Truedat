#requires -Version 7.0
<#
Black-box stdout contract regression; does not build anything.
An audited JSON scan must emit exactly one JSON value, including when a private
catalog is saved and the ordinary end-of-scan summaries run. Human output and
missing-file skip behavior must survive the same routing change.

  pwsh -NoProfile -File tools/tests/test-json-audit.ps1 -TruedatExe <candidate.exe>

Evidence is retained, including on failure. Every child has its own cwd, TEMP,
TMP, stage directory and optional catalog. No process is killed by this script.
#>
[CmdletBinding()]
param(
    [string] $TruedatExe,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $TruedatExe) { $TruedatExe = Join-Path $repoRoot 'dist/truedat/truedat.exe' }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot ('docs/analysis/json-audit-tests/' + [guid]::NewGuid().ToString('N'))
}
$TruedatExe = (Resolve-Path -LiteralPath $TruedatExe).Path
$extractor = Join-Path (Split-Path -Parent $TruedatExe) 'essentia_streaming_extractor_music.exe'
if (-not (Test-Path -LiteralPath $extractor -PathType Leaf)) {
    throw "Place the existing Essentia runtime beside the candidate before testing: $extractor"
}
$fixture = (Resolve-Path -LiteralPath (Join-Path $repoRoot 'tests/edges/fixtures/clip-44k-stereo-short.flac')).Path
# A build that has the work folder (--work-dir, config key work-dir) would put each case's working files under
# <exe folder>\libraries\<id> - beside the candidate exe, often the dist tree. Each case pins them to its own folder, the
# layout these checks read; a build from before the work folder is run as it always was.
$supportsWorkDir = ((& $TruedatExe --config 2>&1) | Out-String) -match 'work-dir'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw "Refusing to reuse evidence directory: $OutputDirectory" }
if ($OutputDirectory -match '[^\x00-\x7F]' -or $fixture -match '[^\x00-\x7F]') {
    throw 'Use an ASCII workspace and evidence path: non-ASCII audio can invoke drive-root hardlink staging.'
}

function Assert-IsolatedStartup {
    # Normal startup sweeps these directories even with --stage-dir. Refuse
    # before every invocation; never clear somebody else's scratch directory.
    foreach ($drive in [IO.DriveInfo]::GetDrives()) {
        if ($drive.IsReady) {
            $hardlinks = Join-Path $drive.RootDirectory.FullName '.truedat-tmp'
            if (Test-Path -LiteralPath $hardlinks) { throw "Unsafe cleanup root already exists: $hardlinks" }
        }
    }
    $siblings = @(Get-Process -ErrorAction Stop | Where-Object { $_.ProcessName -eq 'truedat' })
    if ($siblings.Count) { throw 'Another Truedat process is running; defer this test without stopping it.' }
}

function Get-SourceStamp {
    $item = Get-Item -LiteralPath $fixture
    return [ordered]@{
        path = $fixture; length = $item.Length; lastWriteUtc = $item.LastWriteTimeUtc.ToString('o')
        sha256 = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
    }
}

function Get-AdjacentCatalogStamp {
    # These are the external locations a mistaken default-path resolution could
    # reach. Do not hash a user's large live catalog; detect creation/removal or
    # normal writes through its size/mtime. All intentional writes are private.
    $rows = @()
    $directories = @($repoRoot, (Split-Path -Parent $TruedatExe), (Split-Path -Parent $fixture)) | Sort-Object -Unique
    foreach ($directory in $directories) {
        foreach ($item in Get-ChildItem -LiteralPath $directory -File -Force) {
            if ($item.Name -like 'mbxmoods*' -or $item.Name -like 'catalog.db*') {
                $rows += "$($item.FullName)|$($item.Length)|$($item.LastWriteTimeUtc.Ticks)"
            }
        }
    }
    return ($rows | Sort-Object) -join "`n"
}

$script:checks = [Collections.Generic.List[object]]::new()
$script:runs = [Collections.Generic.List[object]]::new()
function Check([bool] $Condition, [string] $Name) {
    $script:checks.Add([ordered]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { Write-Host "FAIL $Name" -ForegroundColor Red }
}

function Read-StrictJson([string] $Text, [string] $Name) {
    try {
        # Unlike stripping a preamble or extracting braces, this checks the
        # complete stdout text and rejects any second value or trailing summary.
        $document = [System.Text.Json.JsonDocument]::Parse($Text)
        try { Check ($document.RootElement.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) "$Name is one JSON object" }
        finally { $document.Dispose() }
        return ConvertFrom-Json -InputObject $Text -AsHashtable -Depth 100
    }
    catch {
        Check $false "$Name is one complete JSON object ($($_.Exception.Message))"
        return $null
    }
}

function Invoke-Case([string] $Name, [string[]] $ModeArgs, [bool] $Json, [bool] $SaveCatalog,
    [string] $InputText = '', [ValidateSet('off','allow','config')] [string] $StoreMode = 'off', [string] $CatalogTarget = '') {
    Assert-IsolatedStartup
    $folder = Join-Path $OutputDirectory $Name
    $null = New-Item -ItemType Directory -Path $folder
    $tmp = Join-Path $folder 'tmp'
    $stage = Join-Path $folder 'stage'
    $null = New-Item -ItemType Directory -Path $tmp, $stage
    $caseExe = $TruedatExe
    $arguments = @('--no-edges', '--no-exclusions', '--stage-dir', $stage, '-p', '1', '--audit')
    if ($supportsWorkDir) { $arguments = @('--work-dir', $folder) + $arguments }
    if ($StoreMode -eq 'config') {
        # Config is read beside the exe, never from cwd. Copy this small runtime
        # into the case so the test cannot change an installed/user config.
        $privateRuntime = Join-Path $folder 'runtime'
        $null = New-Item -ItemType Directory -Path $privateRuntime
        $caseExe = Join-Path $privateRuntime 'truedat.exe'
        Copy-Item -LiteralPath $TruedatExe -Destination $caseExe
        Copy-Item -LiteralPath $extractor -Destination $privateRuntime
        if (Test-Path -LiteralPath ($TruedatExe + '.config')) {
            Copy-Item -LiteralPath ($TruedatExe + '.config') -Destination ($caseExe + '.config')
        }
        [IO.File]::WriteAllText((Join-Path $privateRuntime 'truedat.config.json'), '{"store":true,"edges":false}')
    }
    else { $arguments += '--no-config' }
    if ($StoreMode -eq 'off') { $arguments += '--no-store' }
    $arguments += $ModeArgs
    if ($Json) { $arguments += '--json-output' }
    $catalog = Join-Path $folder 'mbxmoods.json'
    if ($CatalogTarget) {
        $catalog = [IO.Path]::GetFullPath($CatalogTarget)
        if (-not $catalog.StartsWith($OutputDirectory.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A test catalog must stay inside this evidence directory.'
        }
    }
    if ($SaveCatalog -or $CatalogTarget) { $arguments += @('--moods', $catalog) }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $caseExe
    $start.WorkingDirectory = $folder
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment['TEMP'] = $tmp
    $start.Environment['TMP'] = $tmp
    $null = $start.Environment.Remove('ESSENTIA_PROFILE')
    $null = $start.Environment.Remove('FFREPORT')
    # FFmpeg supports PATH lookup. Essentia requires the adjacent runtime
    # checked above because its FindTool path does not search PATH.
    $runtime = Join-Path $repoRoot 'dist/truedat'
    $start.Environment['PATH'] = $runtime + [IO.Path]::PathSeparator + $start.Environment['PATH']
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Could not start $Name" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if ($InputText) { $process.StandardInput.Write($InputText) }
        $process.StandardInput.Close()
        # Deliberately no timeout/kill: the short fixture bounds normal work,
        # and a hung child must remain visible for operator investigation.
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    }
    finally { $process.Dispose() }
    [IO.File]::WriteAllText((Join-Path $folder 'stdout.txt'), $stdout)
    [IO.File]::WriteAllText((Join-Path $folder 'stderr.txt'), $stderr)
    $run = [ordered]@{ name = $Name; command = @($caseExe) + $arguments; cwd = $folder; exitCode = $exitCode; catalog = $catalog }
    $script:runs.Add($run)
    return @{ stdout = $stdout; stderr = $stderr; exitCode = $exitCode; catalog = $catalog; folder = $folder }
}

function Check-Track($Track, [string] $Name) {
    Check ($null -ne $Track) "$Name has the source track"
    if ($null -eq $Track) { return }
    Check ($Track.audioStreamSha256 -match '^[0-9a-fA-F]{64}$') "$Name has audio identity"
    $fingerprint = $Track['fingerprint.v1']
    Check ($null -ne $fingerprint -and $fingerprint.fileSize -eq $sourceBefore.length -and $fingerprint.durationMs -gt 0) "$Name has a valid fingerprint"
    Check ($Track.ContainsKey('spectralCentroid') -and [double]::IsFinite([double]$Track.spectralCentroid) -and $Track.spectralCentroid -gt 0) "$Name has a finite measured centroid"
}

Assert-IsolatedStartup
$sourceBefore = Get-SourceStamp
$catalogsBefore = Get-AdjacentCatalogStamp
$null = New-Item -ItemType Directory -Path $OutputDirectory
try {
    $single = Invoke-Case 'analyze-json' @('--analyze-file', $fixture) $true $true
    Check ($single.exitCode -eq 0) 'analyze-json exits successfully'
    $payload = Read-StrictJson $single.stdout 'analyze-json stdout'
    if ($null -ne $payload) {
        Check-Track $payload[$fixture] 'analyze-json'
        Check ($payload.audioStreamSha256 -eq $payload[$fixture].audioStreamSha256) 'analyze-json wrapper and track identity agree'
    }
    Check ($single.stderr -match 'DEBUG extract:') 'analyze-json audit diagnostics reach stderr'

    # stdin is the autoscan launcher's real file-list interface. A private
    # catalog exercises ReportCatalog as well as the metadata-summary suffix.
    $batch = Invoke-Case 'file-list-json' @('--file-list', '-') $true $true ($fixture + "`n")
    Check ($batch.exitCode -eq 0) 'file-list-json exits successfully'
    $summary = Read-StrictJson $batch.stdout 'file-list-json stdout'
    if ($null -ne $summary) {
        Check ($summary.processed -eq 1 -and $summary.analyzed -eq 1 -and $summary.cached -eq 0 -and $summary.failed -eq 0) 'file-list-json reports one fresh successful analysis'
    }
    Check ($batch.stderr -match 'DEBUG extract:') 'file-list-json audit diagnostics reach stderr'
    Check ($batch.stderr -match 'Metadata:') 'file-list-json metadata summary reaches stderr'
    Check (Test-Path -LiteralPath $batch.catalog) 'file-list-json writes only its requested private catalog'
    if (Test-Path -LiteralPath $batch.catalog) {
        $catalog = Read-StrictJson ([IO.File]::ReadAllText($batch.catalog)) 'private catalog'
        if ($null -ne $catalog) { Check-Track $catalog.tracks[$fixture] 'private catalog' }
    }

    $human = Invoke-Case 'file-list-human' @('--file-list', '-') $false $false ($fixture + "`n")
    Check ($human.exitCode -eq 0) 'human mode exits successfully'
    Check ($human.stdout -match 'Metadata:') 'human mode keeps its summary on stdout'
    Check ($human.stdout -match 'DEBUG extract:') 'human mode keeps audit diagnostics on stdout'

    $pause = Invoke-Case 'analyze-pause' @('--analyze-file', $fixture, '--pause') $true $false
    Check ($pause.exitCode -eq 0) 'redirected --pause exits successfully without hanging'
    $pausePayload = Read-StrictJson $pause.stdout 'analyze-pause stdout'
    if ($null -ne $pausePayload) { Check-Track $pausePayload[$fixture] 'analyze-pause' }
    Check ($pause.stderr -match 'press any key') 'pause prompt reaches stderr'

    $configured = Invoke-Case 'analyze-config-store' @('--analyze-file', $fixture) $true $false '' 'config'
    Check ($configured.exitCode -eq 0) 'configured store without a catalog remains a successful analysis'
    $configPayload = Read-StrictJson $configured.stdout 'analyze-config-store stdout'
    if ($null -ne $configPayload) { Check-Track $configPayload[$fixture] 'analyze-config-store' }
    Check ($configured.stderr -match 'store skipped this run') 'pre-scan config-store explanation reaches stderr'
    Check (-not (Test-Path -LiteralPath (Join-Path $configured.folder 'truedat.db'))) 'configured store without a target creates no database'

    # Create a real store from the private catalog, then exercise adoption (no
    # explicit --store). This is the other diagnostic before the mode's scope.
    if (Test-Path -LiteralPath $batch.catalog) {
        $storePath = Join-Path $batch.folder 'truedat.db'
        $seed = Invoke-Case 'store-seed' @('--store-import', '--store', $storePath, '--library-root', (Split-Path -Parent $fixture)) $false $false '' 'allow' $batch.catalog
        Check ($seed.exitCode -eq 0 -and (Test-Path -LiteralPath $storePath)) 'private store import succeeds'
        if ($seed.exitCode -eq 0) {
            $adopted = Invoke-Case 'analyze-adopt-store' @('--analyze-file', $fixture) $true $false '' 'allow' $batch.catalog
            Check ($adopted.exitCode -eq 0) 'existing private store is adopted successfully'
            $adoptedPayload = Read-StrictJson $adopted.stdout 'analyze-adopt-store stdout'
            if ($null -ne $adoptedPayload) { Check-Track $adoptedPayload[$fixture] 'analyze-adopt-store' }
            Check ($adopted.stderr -match 'using the store already at') 'pre-scan store-adoption explanation reaches stderr'
        }
    }

    $missing = Join-Path $OutputDirectory 'never-existed.flac'
    $skip = Invoke-Case 'analyze-missing' @('--analyze-file', $missing) $true $false
    Check ($skip.exitCode -eq 0) 'missing analyze-file remains a successful skip'
    Check ([string]::IsNullOrWhiteSpace($skip.stdout)) 'missing analyze-file emits no fabricated JSON result'
    Check ($skip.stderr -match '\[skipped missing\]') 'missing analyze-file explains the skip on stderr'
    Check (Test-Path -LiteralPath (Join-Path $skip.folder 'mbxmoods-skipped.csv')) 'missing analyze-file retains its skip ledger'

    $badList = Invoke-Case 'file-list-missing' @('--file-list', $missing) $true $false
    Check ($badList.exitCode -eq 1) 'missing list remains a usage failure'
    Check ([string]::IsNullOrWhiteSpace($badList.stdout)) 'missing list keeps failure diagnostics out of stdout'
    Check ($badList.stderr -match 'File list not found') 'missing list explains its failure on stderr'
}
catch {
    Check $false "Harness could not finish: $($_.Exception.Message)"
}
finally {
    $sourceAfter = Get-SourceStamp
    Check (($sourceBefore | ConvertTo-Json -Compress) -eq ($sourceAfter | ConvertTo-Json -Compress)) 'committed source fixture is unchanged'
    Check ($catalogsBefore -eq (Get-AdjacentCatalogStamp)) 'adjacent live catalog files were not created, removed or rewritten'
    $report = [ordered]@{
        executable = $TruedatExe; executableSha256 = (Get-FileHash -LiteralPath $TruedatExe -Algorithm SHA256).Hash
        extractor = $extractor; extractorSha256 = (Get-FileHash -LiteralPath $extractor -Algorithm SHA256).Hash
        sourceBefore = $sourceBefore; sourceAfter = $sourceAfter; runs = @($script:runs.ToArray()); checks = @($script:checks.ToArray())
    }
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'evidence.json') -Encoding utf8
}
$failed = @($script:checks | Where-Object { -not $_.passed }).Count
Write-Host "$($script:checks.Count - $failed)/$($script:checks.Count) checks passed. Evidence: $OutputDirectory"
if ($failed) { exit 1 }
