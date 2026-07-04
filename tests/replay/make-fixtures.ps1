# Generates the replay fixture set. Run once; outputs are committed.
# Requires ffmpeg.exe (dist\truedat\ffmpeg.exe or PATH).
param([string]$FfmpegExe = "$PSScriptRoot\..\..\dist\truedat\ffmpeg.exe")
if (-not (Test-Path $FfmpegExe)) { $FfmpegExe = "ffmpeg" }
$out = "$PSScriptRoot\fixtures"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 1. Standard: 44.1k/16 stereo, beeping sine (beep_factor gives onsets -> non-degenerate bpm/onsetRate)
& $FfmpegExe -y -f lavfi -i "sine=frequency=440:beep_factor=4:duration=40" -ac 2 -ar 44100 -sample_fmt s16 "$out\fixture-standard.flac"
# 2. Hi-res: 96k/24, pink noise + tone (pink noise has real energy above 22.05kHz -> hfAnalysis populated; 24-bit flac -> bitUsage valid)
& $FfmpegExe -y -f lavfi -i "anoisesrc=color=pink:seed=42:duration=40" -f lavfi -i "sine=frequency=880:beep_factor=3:duration=40" -filter_complex "[0][1]amix=inputs=2" -ac 2 -ar 96000 -sample_fmt s32 -c:a flac "$out\fixture-hires.flac"
# 3. Lossy: mp3 via libmp3lame (produces Xing+LAME header -> mp3LameTag path)
& $FfmpegExe -y -f lavfi -i "sine=frequency=440:beep_factor=4:duration=40" -ac 2 -ar 44100 -c:a libmp3lame -b:a 192k -write_xing 1 "$out\fixture-lossy.mp3"
Get-ChildItem $out | Format-Table Name, Length
