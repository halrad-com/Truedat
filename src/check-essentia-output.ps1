# Check what Essentia's highlevel mood classifiers output for one track

$essentiaExe = 'C:\test\MB2\MusicBee\Library\essentia_streaming_extractor_music.exe'
$moods = Get-Content 'C:\test\MB2\MusicBee\Library\mbxmoods.json' -Raw | ConvertFrom-Json
$firstTrack = $moods.tracks.PSObject.Properties | Select-Object -First 1
$audioPath = "$env:USERPROFILE\Music\Deep Purple\Burn\01 Burn.mp3"  # Known to exist
$tempJson = "$env:TEMP\essentia_test_output.json"

Write-Host "Testing: $($firstTrack.Value.artist) - $($firstTrack.Value.title)"
Write-Host "Audio: $audioPath"
Write-Host ""

if (-not (Test-Path $audioPath)) {
    Write-Host "Audio file not found!" -ForegroundColor Red
    exit 1
}

Write-Host "Running Essentia (this takes a few seconds)..."
$proc = Start-Process -FilePath $essentiaExe -ArgumentList "`"$audioPath`"", "`"$tempJson`"" -Wait -PassThru -NoNewWindow -RedirectStandardError "$env:TEMP\essentia_stderr.txt"
Write-Host "Exit code: $($proc.ExitCode)"

if (-not (Test-Path $tempJson)) {
    Write-Host "No output file produced" -ForegroundColor Red
    $stderr = Get-Content "$env:TEMP\essentia_stderr.txt" -Raw
    Write-Host $stderr
    exit 1
}

$size = (Get-Item $tempJson).Length
Write-Host "Output: $size bytes"
Write-Host ""

# Parse with Newtonsoft-style via raw text since PowerShell JSON can be flaky with large files
$root = Get-Content $tempJson -Raw | ConvertFrom-Json

# Show top-level keys
Write-Host "=== TOP-LEVEL KEYS ===" -ForegroundColor Cyan
$root.PSObject.Properties | ForEach-Object { Write-Host "  $($_.Name)" }

# Check for highlevel
if ($root.highlevel) {
    Write-Host ""
    Write-Host "=== HIGHLEVEL CLASSIFIERS ===" -ForegroundColor Cyan
    $root.highlevel.PSObject.Properties | ForEach-Object {
        $name = $_.Name
        $val = $_.Value
        Write-Host "  $name`: $($val.value)"
        if ($val.all) {
            $val.all.PSObject.Properties | ForEach-Object {
                Write-Host "    $($_.Name) = $($_.Value)"
            }
        }
    }
} else {
    Write-Host ""
    Write-Host "NO 'highlevel' section found" -ForegroundColor Red
    Write-Host "This Essentia build may not include SVM mood models"
}

# Show rhythm section
if ($root.rhythm) {
    Write-Host ""
    Write-Host "=== RHYTHM (sample) ===" -ForegroundColor Yellow
    @("bpm", "danceability", "onset_rate", "beats_count") | ForEach-Object {
        $val = $root.rhythm.PSObject.Properties[$_]
        if ($val) { Write-Host "  $($_): $($val.Value)" }
    }
}

# Clean up
Remove-Item $tempJson -Force -ErrorAction SilentlyContinue
Remove-Item "$env:TEMP\essentia_stderr.txt" -Force -ErrorAction SilentlyContinue
