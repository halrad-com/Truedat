# analyze-moods.ps1 - Replicate MoodEstimator logic on mbxmoods.json to diagnose clustering
# Uses the CURRENT settings from mbxhub.json

param(
    [string]$MoodsFile = "C:\test\MB2\MusicBee\Library\mbxmoods.json",
    [string]$SettingsFile = "C:\test\MB2\MusicBee\AppData\MBXHub\mbxhub.json"
)

$moods = Get-Content $MoodsFile -Raw | ConvertFrom-Json
$settings = (Get-Content $SettingsFile -Raw | ConvertFrom-Json).autoQ.estimation

Write-Host "=== MOOD ESTIMATOR ANALYSIS ===" -ForegroundColor Cyan
Write-Host "Tracks: $($moods.trackCount)"
Write-Host ""

# --- First: show the weight sums (should be ~1.0) ---
$valWeightSum = $settings.valenceWeightMode + $settings.valenceWeightCentroid + $settings.valenceWeightDance +
    $settings.valenceWeightFlatness + $settings.valenceWeightDissonance + $settings.valenceWeightPitchSalience +
    $settings.valenceWeightChords + $settings.valenceWeightMfcc
$aroWeightSum = $settings.arousalWeightBpm + $settings.arousalWeightLoudness + $settings.arousalWeightFlux +
    $settings.arousalWeightDance + $settings.arousalWeightOnsetRate + $settings.arousalWeightZcr +
    $settings.arousalWeightRms

Write-Host "=== WEIGHT SUMS (should be ~1.0) ===" -ForegroundColor Yellow
Write-Host "  Valence weights sum: $([math]::Round($valWeightSum, 3))  $(if($valWeightSum -gt 1.05){'*** TOO HIGH - causes clipping ***'} elseif($valWeightSum -lt 0.95){'*** TOO LOW - compressed range ***'})" -ForegroundColor $(if($valWeightSum -gt 1.05 -or $valWeightSum -lt 0.95){'Red'}else{'Green'})
Write-Host "  Arousal weights sum: $([math]::Round($aroWeightSum, 3))  $(if($aroWeightSum -gt 1.05){'*** TOO HIGH - causes clipping ***'} elseif($aroWeightSum -lt 0.95){'*** TOO LOW - compressed range ***'})" -ForegroundColor $(if($aroWeightSum -gt 1.05 -or $aroWeightSum -lt 0.95){'Red'}else{'Green'})
Write-Host ""

# --- Compute valence/arousal for each track exactly as MoodEstimator does ---
function Clamp01($v) { [math]::Max(0, [math]::Min(1, $v)) }

$results = @()
$rawFeatures = @{
    bpm=@(); loudness=@(); centroid=@(); flux=@(); dance=@(); onset=@()
    zcr=@(); rms=@(); flatness=@(); dissonance=@(); salience=@(); chords=@(); mfcc2=@()
}

foreach ($prop in $moods.tracks.PSObject.Properties) {
    $t = $prop.Value
    $hasRaw = ($t.spectralCentroid -gt 0) -or ($t.loudness -ne 0) -or ($t.danceability -gt 0)
    if (-not $hasRaw) { continue }

    # Collect raw feature values for distribution analysis
    $rawFeatures.bpm += $t.bpm
    $rawFeatures.loudness += $t.loudness
    $rawFeatures.centroid += $t.spectralCentroid
    $rawFeatures.flux += $t.spectralFlux
    $rawFeatures.dance += $t.danceability
    $rawFeatures.onset += $t.onsetRate
    $rawFeatures.zcr += $t.zeroCrossingRate
    $rawFeatures.rms += $t.spectralRms
    $rawFeatures.flatness += $t.spectralFlatness
    $rawFeatures.dissonance += $t.dissonance
    $rawFeatures.salience += $t.pitchSalience
    $rawFeatures.chords += $t.chordsChangesRate
    if ($t.mfcc -and $t.mfcc.Count -gt 1) { $rawFeatures.mfcc2 += $t.mfcc[1] }

    # Compute valence (replicating MoodEstimator.ComputeValence)
    $modeScore = if ($t.mode -eq "major") { $settings.modeScoreMajor } else { $settings.modeScoreMinor }
    $centroidRange = $settings.centroidMax - $settings.centroidMin
    $centroidNorm = if ($centroidRange -gt 0) { Clamp01(($t.spectralCentroid - $settings.centroidMin) / $centroidRange) } else { 0.5 }
    $danceNorm = if ($settings.danceMax -gt 0) { Clamp01($t.danceability / $settings.danceMax) } else { 0.5 }
    $flatnessNorm = 1.0 - (Clamp01($t.spectralFlatness))
    $dissonanceNorm = 1.0 - (Clamp01($t.dissonance))
    $salienceNorm = Clamp01($t.pitchSalience)
    $chordsNorm = if ($settings.chordsRateMax -gt 0) { Clamp01($t.chordsChangesRate / $settings.chordsRateMax) } else { 0.5 }
    $mfccNorm = 0.5
    $mfccRange = $settings.mfccMax - $settings.mfccMin
    if ($t.mfcc -and $t.mfcc.Count -gt 1 -and $mfccRange -gt 0) {
        $mfccNorm = Clamp01(($t.mfcc[1] - $settings.mfccMin) / $mfccRange)
    }

    $valenceRaw = $settings.valenceWeightMode * $modeScore +
        $settings.valenceWeightCentroid * $centroidNorm +
        $settings.valenceWeightDance * $danceNorm +
        $settings.valenceWeightFlatness * $flatnessNorm +
        $settings.valenceWeightDissonance * $dissonanceNorm +
        $settings.valenceWeightPitchSalience * $salienceNorm +
        $settings.valenceWeightChords * $chordsNorm +
        $settings.valenceWeightMfcc * $mfccNorm
    $valence = Clamp01($valenceRaw)

    # Compute arousal (replicating MoodEstimator.ComputeArousal)
    $bpmRange = $settings.bpmMax - $settings.bpmMin
    $bpmNorm = if ($bpmRange -gt 0) { Clamp01(($t.bpm - $settings.bpmMin) / $bpmRange) } else { 0.5 }
    $loudRange = $settings.loudnessMax - $settings.loudnessMin
    $loudNorm = if ($loudRange -gt 0) { Clamp01(($t.loudness - $settings.loudnessMin) / $loudRange) } else { 0.5 }
    $fluxNorm = if ($settings.fluxMax -gt 0) { Clamp01($t.spectralFlux / $settings.fluxMax) } else { 0.5 }
    $danceNormA = if ($settings.danceMax -gt 0) { Clamp01($t.danceability / $settings.danceMax) } else { 0.5 }
    $onsetNorm = if ($settings.onsetRateMax -gt 0) { Clamp01($t.onsetRate / $settings.onsetRateMax) } else { 0.5 }
    $zcrNorm = if ($settings.zcrMax -gt 0) { Clamp01($t.zeroCrossingRate / $settings.zcrMax) } else { 0.5 }
    $rmsNorm = if ($settings.rmsMax -gt 0) { Clamp01($t.spectralRms / $settings.rmsMax) } else { 0.5 }

    $arousalRaw = $settings.arousalWeightBpm * $bpmNorm +
        $settings.arousalWeightLoudness * $loudNorm +
        $settings.arousalWeightFlux * $fluxNorm +
        $settings.arousalWeightDance * $danceNormA +
        $settings.arousalWeightOnsetRate * $onsetNorm +
        $settings.arousalWeightZcr * $zcrNorm +
        $settings.arousalWeightRms * $rmsNorm
    $arousal = Clamp01($arousalRaw)

    $results += [PSCustomObject]@{
        Artist = $t.artist
        Title = $t.title
        Genre = $t.genre
        Valence = [math]::Round($valence, 4)
        Arousal = [math]::Round($arousal, 4)
        ValenceRaw = [math]::Round($valenceRaw, 4)
        ArousalRaw = [math]::Round($arousalRaw, 4)
        Mode = $t.mode
        BPM = $t.bpm
    }
}

Write-Host "=== RAW FEATURE RANGES (your library p5/p50/p95) ===" -ForegroundColor Yellow
Write-Host ("  vs current normalization settings") -ForegroundColor DarkGray
foreach ($feat in @('bpm','loudness','centroid','flux','dance','onset','zcr','rms','flatness','dissonance','salience','chords','mfcc2')) {
    $vals = $rawFeatures[$feat] | Sort-Object
    if ($vals.Count -eq 0) { continue }
    $p5 = $vals[[math]::Floor($vals.Count * 0.05)]
    $p25 = $vals[[math]::Floor($vals.Count * 0.25)]
    $p50 = $vals[[math]::Floor($vals.Count * 0.50)]
    $p75 = $vals[[math]::Floor($vals.Count * 0.75)]
    $p95 = $vals[[math]::Floor($vals.Count * 0.95)]
    $min = $vals[0]; $max = $vals[-1]

    # Show which setting controls this
    $settingInfo = switch ($feat) {
        'bpm'        { "range: [$($settings.bpmMin), $($settings.bpmMax)]" }
        'loudness'   { "range: [$($settings.loudnessMin), $($settings.loudnessMax)]" }
        'centroid'   { "range: [$($settings.centroidMin), $($settings.centroidMax)]" }
        'flux'       { "max: $($settings.fluxMax)" }
        'dance'      { "max: $($settings.danceMax)" }
        'onset'      { "max: $($settings.onsetRateMax)" }
        'zcr'        { "max: $($settings.zcrMax)" }
        'rms'        { "max: $($settings.rmsMax)" }
        'chords'     { "max: $($settings.chordsRateMax)" }
        'mfcc2'      { "range: [$($settings.mfccMin), $($settings.mfccMax)]" }
        default      { "" }
    }

    Write-Host ("  {0,-12} p5={1,10:F4}  p25={2,10:F4}  p50={3,10:F4}  p75={4,10:F4}  p95={5,10:F4}  [min={6:F4} max={7:F4}]  setting {8}" -f $feat, $p5, $p25, $p50, $p75, $p95, $min, $max, $settingInfo)
}

Write-Host ""
Write-Host "=== COMPUTED MOOD DISTRIBUTION ===" -ForegroundColor Yellow
$valences = $results | ForEach-Object { $_.Valence } | Sort-Object
$arousals = $results | ForEach-Object { $_.Arousal } | Sort-Object
$valRaw = $results | ForEach-Object { $_.ValenceRaw } | Sort-Object
$aroRaw = $results | ForEach-Object { $_.ArousalRaw } | Sort-Object

function ShowDist($name, $vals) {
    $p5 = $vals[[math]::Floor($vals.Count * 0.05)]
    $p25 = $vals[[math]::Floor($vals.Count * 0.25)]
    $p50 = $vals[[math]::Floor($vals.Count * 0.50)]
    $p75 = $vals[[math]::Floor($vals.Count * 0.75)]
    $p95 = $vals[[math]::Floor($vals.Count * 0.95)]
    $spread = $p95 - $p5
    $iqr = $p75 - $p25
    Write-Host ("  {0,-18} p5={1:F3}  p25={2:F3}  p50={3:F3}  p75={4:F3}  p95={5:F3}  spread={6:F3}  IQR={7:F3}" -f $name, $p5, $p25, $p50, $p75, $p95, $spread, $iqr)
}

ShowDist "Valence (clamped)" $valences
ShowDist "Arousal (clamped)" $arousals
ShowDist "Valence (raw)" $valRaw
ShowDist "Arousal (raw)" $aroRaw

# Count clipping
$valClipHigh = ($results | Where-Object { $_.ValenceRaw -ge 1.0 }).Count
$valClipLow = ($results | Where-Object { $_.ValenceRaw -le 0.0 }).Count
$aroClipHigh = ($results | Where-Object { $_.ArousalRaw -ge 1.0 }).Count
$aroClipLow = ($results | Where-Object { $_.ArousalRaw -le 0.0 }).Count
$total = $results.Count

Write-Host ""
Write-Host "=== CLIPPING (values hitting 0.0 or 1.0 ceiling) ===" -ForegroundColor Yellow
Write-Host "  Valence clipped HIGH (>=1.0): $valClipHigh / $total ($([math]::Round($valClipHigh/$total*100,1))%)" -ForegroundColor $(if($valClipHigh/$total -gt 0.1){'Red'}else{'Green'})
Write-Host "  Valence clipped LOW  (<=0.0): $valClipLow / $total ($([math]::Round($valClipLow/$total*100,1))%)" -ForegroundColor $(if($valClipLow/$total -gt 0.1){'Red'}else{'Green'})
Write-Host "  Arousal clipped HIGH (>=1.0): $aroClipHigh / $total ($([math]::Round($aroClipHigh/$total*100,1))%)" -ForegroundColor $(if($aroClipHigh/$total -gt 0.1){'Red'}else{'Green'})
Write-Host "  Arousal clipped LOW  (<=0.0): $aroClipLow / $total ($([math]::Round($aroClipLow/$total*100,1))%)" -ForegroundColor $(if($aroClipLow/$total -gt 0.1){'Red'}else{'Green'})

# Mood channel assignment
Write-Host ""
Write-Host "=== MOOD CHANNEL DISTRIBUTION ===" -ForegroundColor Yellow
$channels = @(
    @{Name="Energetic"; Arousal=0.90; Valence=0.80}
    @{Name="Dance";     Arousal=0.85; Valence=0.75}
    @{Name="Intense";   Arousal=0.75; Valence=0.35}
    @{Name="Upbeat";    Arousal=0.70; Valence=0.80}
    @{Name="Morning";   Arousal=0.55; Valence=0.75}
    @{Name="Chill";     Arousal=0.35; Valence=0.65}
    @{Name="Mellow";    Arousal=0.25; Valence=0.55}
    @{Name="Night";     Arousal=0.20; Valence=0.35}
    @{Name="Emotional"; Arousal=0.40; Valence=0.30}
    @{Name="Relax";     Arousal=0.15; Valence=0.60}
)

$channelCounts = @{}
$channels | ForEach-Object { $channelCounts[$_.Name] = 0 }
$unmatched = 0
$minSim = 0.5

foreach ($r in $results) {
    $bestName = $null; $bestSim = 0
    foreach ($ch in $channels) {
        $dv = $r.Valence - $ch.Valence
        $da = $r.Arousal - $ch.Arousal
        $dist = [math]::Sqrt($dv*$dv + $da*$da)
        $sim = 1.0 - ($dist / [math]::Sqrt(2))
        if ($sim -gt $bestSim) { $bestSim = $sim; $bestName = $ch.Name }
    }
    if ($bestName -and $bestSim -gt $minSim) { $channelCounts[$bestName]++ } else { $unmatched++ }
}

foreach ($ch in $channels) {
    $count = $channelCounts[$ch.Name]
    $pct = [math]::Round($count / $total * 100, 1)
    $bar = "#" * [math]::Min(50, [math]::Round($pct))
    Write-Host ("  {0,-12} {1,5} ({2,5:F1}%) {3}" -f $ch.Name, $count, $pct, $bar) -ForegroundColor $(if($pct -gt 30){'Red'}elseif($pct -lt 2){'DarkGray'}else{'White'})
}
Write-Host ("  {0,-12} {1,5} ({2,5:F1}%)" -f "Unmatched", $unmatched, [math]::Round($unmatched/$total*100,1)) -ForegroundColor DarkGray

# Show some extreme examples
Write-Host ""
Write-Host "=== SAMPLE TRACKS (edge cases) ===" -ForegroundColor Yellow
Write-Host "--- Highest Valence ---"
$results | Sort-Object -Property Valence -Descending | Select-Object -First 5 | ForEach-Object {
    Write-Host ("  V={0:F3} A={1:F3} [{2}] {3} - {4}" -f $_.Valence, $_.Arousal, $_.Mode, $_.Artist, $_.Title)
}
Write-Host "--- Lowest Valence ---"
$results | Sort-Object -Property Valence | Select-Object -First 5 | ForEach-Object {
    Write-Host ("  V={0:F3} A={1:F3} [{2}] {3} - {4}" -f $_.Valence, $_.Arousal, $_.Mode, $_.Artist, $_.Title)
}
Write-Host "--- Highest Arousal ---"
$results | Sort-Object -Property Arousal -Descending | Select-Object -First 5 | ForEach-Object {
    Write-Host ("  V={0:F3} A={1:F3} [{2}] {3} - {4}" -f $_.Valence, $_.Arousal, $_.Mode, $_.Artist, $_.Title)
}
Write-Host "--- Lowest Arousal ---"
$results | Sort-Object -Property Arousal | Select-Object -First 5 | ForEach-Object {
    Write-Host ("  V={0:F3} A={1:F3} [{2}] {3} - {4}" -f $_.Valence, $_.Arousal, $_.Mode, $_.Artist, $_.Title)
}

Write-Host ""
Write-Host "=== DIAGNOSIS ===" -ForegroundColor Cyan
$issues = @()
if ($valWeightSum -gt 1.05) { $issues += "Valence weights sum to $([math]::Round($valWeightSum,2)) (>1.0) - raw values exceed 1.0 then get clipped, compressing differences" }
if ($aroWeightSum -gt 1.05) { $issues += "Arousal weights sum to $([math]::Round($aroWeightSum,2)) (>1.0) - same clipping problem" }
if ($valClipHigh/$total -gt 0.1) { $issues += "$([math]::Round($valClipHigh/$total*100))% of valence values clipping at 1.0" }
if ($aroClipHigh/$total -gt 0.1) { $issues += "$([math]::Round($aroClipHigh/$total*100))% of arousal values clipping at 1.0" }

# Check normalization ranges vs actual data
$p5bpm = ($rawFeatures.bpm | Sort-Object)[[math]::Floor($rawFeatures.bpm.Count * 0.05)]
$p95bpm = ($rawFeatures.bpm | Sort-Object)[[math]::Floor($rawFeatures.bpm.Count * 0.95)]
if ($settings.bpmMin -lt $p5bpm * 0.7) { $issues += "bpmMin ($($settings.bpmMin)) is much lower than data p5 ($([math]::Round($p5bpm,1))) - compresses BPM range" }
if ($settings.bpmMax -gt $p95bpm * 1.3) { $issues += "bpmMax ($($settings.bpmMax)) is much higher than data p95 ($([math]::Round($p95bpm,1))) - compresses BPM range" }

$p5loud = ($rawFeatures.loudness | Sort-Object)[[math]::Floor($rawFeatures.loudness.Count * 0.05)]
$p95loud = ($rawFeatures.loudness | Sort-Object)[[math]::Floor($rawFeatures.loudness.Count * 0.95)]
if ($settings.loudnessMin -lt $p5loud * 1.5) { $issues += "loudnessMin ($($settings.loudnessMin)) is way below data p5 ($([math]::Round($p5loud,1))) - loudness barely varies" }

$p95rms = ($rawFeatures.rms | Sort-Object)[[math]::Floor($rawFeatures.rms.Count * 0.95)]
if ($settings.rmsMax -gt $p95rms * 10) { $issues += "rmsMax ($($settings.rmsMax)) is $([math]::Round($settings.rmsMax / $p95rms))x the data p95 ($([math]::Round($p95rms,6))) - RMS always near zero" }

$p95chords = ($rawFeatures.chords | Sort-Object)[[math]::Floor($rawFeatures.chords.Count * 0.95)]
if ($settings.chordsRateMax -gt $p95chords * 5) { $issues += "chordsRateMax ($($settings.chordsRateMax)) is $([math]::Round($settings.chordsRateMax / $p95chords))x the data p95 ($([math]::Round($p95chords,4))) - chords always near zero" }

$p95onset = ($rawFeatures.onset | Sort-Object)[[math]::Floor($rawFeatures.onset.Count * 0.95)]
if ($settings.onsetRateMax -gt $p95onset * 2) { $issues += "onsetRateMax ($($settings.onsetRateMax)) is $([math]::Round($settings.onsetRateMax / $p95onset,1))x the data p95 ($([math]::Round($p95onset,2))) - onset compressed" }

if ($settings.mfccMin -lt 0 -and ($rawFeatures.mfcc2 | Measure-Object -Minimum).Minimum -gt 50) {
    $issues += "mfccMin ($($settings.mfccMin)) is negative but all MFCC2 values are positive ($([math]::Round(($rawFeatures.mfcc2 | Measure-Object -Minimum).Minimum,1))-$([math]::Round(($rawFeatures.mfcc2 | Measure-Object -Maximum).Maximum,1))) - MFCC always clips to 1.0"
}

$modeSplit = ($results | Where-Object { $_.Mode -eq "major" }).Count / $total * 100
if ($settings.modeScoreMajor - $settings.modeScoreMinor -gt 0.5) {
    $issues += "Mode cliff: major=$($settings.modeScoreMajor) minor=$($settings.modeScoreMinor) (gap=$($settings.modeScoreMajor - $settings.modeScoreMinor)) with $([math]::Round($modeSplit))% major tracks - creates binary valence split"
}

if ($issues.Count -gt 0) {
    Write-Host "FOUND $($issues.Count) ISSUES:" -ForegroundColor Red
    foreach ($issue in $issues) {
        Write-Host "  - $issue" -ForegroundColor Red
    }
} else {
    Write-Host "No obvious issues detected." -ForegroundColor Green
}
