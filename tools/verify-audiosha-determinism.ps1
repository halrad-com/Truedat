# verify-audiosha-determinism.ps1 — cross-machine audioStreamSha256 sanity rig.
#
# Enumerates audio files under -InputDir, runs truedat in --hash-only --level stream
# mode, and writes one identity envelope per file (NDJSON) to -OutputManifest.
# Run on two machines holding byte-identical input directories; diff the manifests.
# When the audioStreamSha256 values match line-for-line (matching by "path" basename
# or by file ordering), cross-machine determinism is verified.
#
# Pairs with the offline --hash-only --output flag landed in the same commit.
# No HTTP server required — the rig is fully offline.
#
# Usage:
#   pwsh -File tools/verify-audiosha-determinism.ps1 `
#       -InputDir D:\test-corpus -OutputManifest C:\out\manifest-machineA.ndjson
#
# Optional flags:
#   -TruedatExe <path>     Override the default ../dist/truedat/truedat.exe path
#   -Parallelism <n>       Pass through to truedat -p (default: truedat's auto)

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)] [string] $InputDir,
    [Parameter(Mandatory=$true)] [string] $OutputManifest,
    [string] $TruedatExe = "$PSScriptRoot\..\dist\truedat\truedat.exe",
    [int] $Parallelism = 0
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $InputDir)) {
    Write-Error "InputDir not found: $InputDir"
    exit 1
}
if (-not (Test-Path $TruedatExe)) {
    Write-Error "truedat.exe not found at: $TruedatExe (set -TruedatExe to override)"
    exit 1
}

# Audio extensions truedat scans — keep in sync with what mainstream MusicBee libraries hold.
$audioExts = @('.flac','.mp3','.m4a','.aac','.ogg','.opus','.wav','.wma','.ape')

$files = Get-ChildItem -Path $InputDir -Recurse -File |
    Where-Object { $audioExts -contains $_.Extension.ToLower() } |
    Sort-Object FullName  # deterministic ordering for diff-friendly output

if ($files.Count -eq 0) {
    Write-Error "No audio files found under $InputDir"
    exit 1
}

Write-Host "Found $($files.Count) audio files under $InputDir"

# Write the file list to a temp file truedat can consume via --file-list.
$fileList = New-TemporaryFile
try {
    $files.FullName | Set-Content -Path $fileList -Encoding UTF8

    $argList = @(
        '--hash-only',
        '--level', 'stream',
        '--file-list', $fileList,
        '--output', $OutputManifest
    )
    if ($Parallelism -gt 0) { $argList += @('-p', $Parallelism) }

    Write-Host "Running: $TruedatExe $($argList -join ' ')"
    & $TruedatExe @argList
    $exit = $LASTEXITCODE
}
finally {
    Remove-Item $fileList -Force -ErrorAction SilentlyContinue
}

if ($exit -ne 0) {
    Write-Error "truedat exited with code $exit"
    exit 1
}

if (-not (Test-Path $OutputManifest)) {
    Write-Error "Expected manifest not produced: $OutputManifest"
    exit 1
}

$lineCount = (Get-Content -LiteralPath $OutputManifest | Measure-Object -Line).Lines
Write-Host "Manifest written: $OutputManifest ($lineCount lines)"
exit 0
