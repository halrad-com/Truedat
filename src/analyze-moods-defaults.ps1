# analyze-moods-defaults.ps1 - Run mood analysis using CODE DEFAULTS from EstimationSettings.cs

param(
    [string]$MoodsFile = "C:\test\MB2\MusicBee\Library\mbxmoods.json"
)

$moods = Get-Content $MoodsFile -Raw | ConvertFrom-Json

# Code defaults from EstimationSettings.cs
$est = @{
    bpmMin = 80; bpmMax = 170
    centroidMin = 400; centroidMax = 2500
    loudnessMin = -25; loudnessMax = -5
    onsetRateMax = 6.0; zcrMax = 0.15; rmsMax = 0.01
    chordsRateMax = 0.2; fluxMax = 0.15; danceMax = 2.0
    mfccMin = 50; mfccMax = 250
    modeScoreMajor = 0.8; modeScoreMinor = 0.4
    valenceWeightMode = 0.25; valenceWeightCentroid = 0.15; valenceWeightDance = 0.10
    valenceWeightFlatness = 0.0; valenceWeightDissonance = 0.20
    valenceWeightPitchSalience = 0.15; valenceWeightChords = 0.05; valenceWeightMfcc = 0.10
    arousalWeightBpm = 0.20; arousalWeightLoudness = 0.15; arousalWeightFlux = 0.15
    arousalWeightDance = 0.10; arousalWeightOnsetRate = 0.15; arousalWeightZcr = 0.10
    arousalWeightRms = 0.15
}

$valWeightSum = $est.valenceWeightMode + $est.valenceWeightCentroid + $est.valenceWeightDance +
    $est.valenceWeightFlatness + $est.valenceWeightDissonance + $est.valenceWeightPitchSalience +
    $est.valenceWeightChords + $est.valenceWeightMfcc
$aroWeightSum = $est.arousalWeightBpm + $est.arousalWeightLoudness + $est.arousalWeightFlux +
    $est.arousalWeightDance + $est.arousalWeightOnsetRate + $est.arousalWeightZcr + $est.arousalWeightRms

Write-Host "=== MOOD ANALYSIS WITH CODE DEFAULTS ===" -ForegroundColor Cyan
Write-Host "Tracks: $($moods.trackCount)"
Write-Host "Valence weights sum: $([math]::Round($valWeightSum, 3))" -ForegroundColor Green
Write-Host "Arousal weights sum: $([math]::Round($aroWeightSum, 3))" -ForegroundColor Green
Write-Host ""

function Clamp01($v) { [math]::Max(0, [math]::Min(1, $v)) }

$results = @()
foreach ($prop in $moods.tracks.PSObject.Properties) {
    $t = $prop.Value
    $hasRaw = ($t.spectralCentroid -gt 0) -or ($t.loudness -ne 0) -or ($t.danceability -gt 0)
    if (-not $hasRaw) { continue }

    $modeScore = if ($t.mode -eq "major") { $est.modeScoreMajor } else { $est.modeScoreMinor }
    $centroidRange = $est.centroidMax - $est.centroidMin
    $centroidNorm = if ($centroidRange -gt 0) { Clamp01(($t.spectralCentroid - $est.centroidMin) / $centroidRange) } else { 0.5 }
    $danceNorm = if ($est.danceMax -gt 0) { Clamp01($t.danceability / $est.danceMax) } else { 0.5 }
    $flatnessNorm = 1.0 - (Clamp01($t.spectralFlatness))
    $dissonanceNorm = 1.0 - (Clamp01($t.dissonance))
    $salienceNorm = Clamp01($t.pitchSalience)
    $chordsNorm = if ($est.chordsRateMax -gt 0) { Clamp01($t.chordsChangesRate / $est.chordsRateMax) } else { 0.5 }
    $mfccNorm = 0.5
    $mfccRange = $est.mfccMax - $est.mfccMin
    if ($t.mfcc -and $t.mfcc.Count -gt 1 -and $mfccRange -gt 0) {
        $mfccNorm = Clamp01(($t.mfcc[1] - $est.mfccMin) / $mfccRange)
    }

    $valenceRaw = $est.valenceWeightMode * $modeScore +
        $est.valenceWeightCentroid * $centroidNorm +
        $est.valenceWeightDance * $danceNorm +
        $est.valenceWeightFlatness * $flatnessNorm +
        $est.valenceWeightDissonance * $dissonanceNorm +
        $est.valenceWeightPitchSalience * $salienceNorm +
        $est.valenceWeightChords * $chordsNorm +
        $est.valenceWeightMfcc * $mfccNorm
    $valence = Clamp01($valenceRaw)

    $bpmRange = $est.bpmMax - $est.bpmMin
    $bpmNorm = if ($bpmRange -gt 0) { Clamp01(($t.bpm - $est.bpmMin) / $bpmRange) } else { 0.5 }
    $loudRange = $est.loudnessMax - $est.loudnessMin
    $loudNorm = if ($loudRange -gt 0) { Clamp01(($t.loudness - $est.loudnessMin) / $loudRange) } else { 0.5 }
    $fluxNorm = if ($est.fluxMax -gt 0) { Clamp01($t.spectralFlux / $est.fluxMax) } else { 0.5 }
    $danceNormA = if ($est.danceMax -gt 0) { Clamp01($t.danceability / $est.danceMax) } else { 0.5 }
    $onsetNorm = if ($est.onsetRateMax -gt 0) { Clamp01($t.onsetRate / $est.onsetRateMax) } else { 0.5 }
    $zcrNorm = if ($est.zcrMax -gt 0) { Clamp01($t.zeroCrossingRate / $est.zcrMax) } else { 0.5 }
    $rmsNorm = if ($est.rmsMax -gt 0) { Clamp01($t.spectralRms / $est.rmsMax) } else { 0.5 }

    $arousalRaw = $est.arousalWeightBpm * $bpmNorm +
        $est.arousalWeightLoudness * $loudNorm +
        $est.arousalWeightFlux * $fluxNorm +
        $est.arousalWeightDance * $danceNormA +
        $est.arousalWeightOnsetRate * $onsetNorm +
        $est.arousalWeightZcr * $zcrNorm +
        $est.arousalWeightRms * $rmsNorm
    $arousal = Clamp01($arousalRaw)

    $results += [PSCustomObject]@{
        Artist = $t.artist; Title = $t.title; Genre = $t.genre
        Valence = [math]::Round($valence, 4); Arousal = [math]::Round($arousal, 4)
        ValenceRaw = [math]::Round($valenceRaw, 4); ArousalRaw = [math]::Round($arousalRaw, 4)
        Mode = $t.mode; BPM = $t.bpm
    }
}

$total = $results.Count

Write-Host "=== COMPUTED MOOD DISTRIBUTION ===" -ForegroundColor Yellow
$valences = $results | ForEach-Object { $_.Valence } | Sort-Object
$arousals = $results | ForEach-Object { $_.Arousal } | Sort-Object

function ShowDist($name, $vals) {
    $p5 = $vals[[math]::Floor($vals.Count * 0.05)]
    $p25 = $vals[[math]::Floor($vals.Count * 0.25)]
    $p50 = $vals[[math]::Floor($vals.Count * 0.50)]
    $p75 = $vals[[math]::Floor($vals.Count * 0.75)]
    $p95 = $vals[[math]::Floor($vals.Count * 0.95)]
    Write-Host ("  {0,-18} p5={1:F3}  p25={2:F3}  p50={3:F3}  p75={4:F3}  p95={5:F3}  spread={6:F3}  IQR={7:F3}" -f $name, $p5, $p25, $p50, $p75, $p95, ($p95-$p5), ($p75-$p25))
}

ShowDist "Valence" $valences
ShowDist "Arousal" $arousals

$valClipHigh = ($results | Where-Object { $_.ValenceRaw -ge 1.0 }).Count
$aroClipHigh = ($results | Where-Object { $_.ArousalRaw -ge 1.0 }).Count
Write-Host ""
Write-Host "=== CLIPPING ===" -ForegroundColor Yellow
Write-Host "  Valence clipped HIGH: $valClipHigh / $total ($([math]::Round($valClipHigh/$total*100,1))%)" -ForegroundColor $(if($valClipHigh/$total -gt 0.05){'Red'}else{'Green'})
Write-Host "  Arousal clipped HIGH: $aroClipHigh / $total ($([math]::Round($aroClipHigh/$total*100,1))%)" -ForegroundColor $(if($aroClipHigh/$total -gt 0.05){'Red'}else{'Green'})

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

$channelCounts = @{}; $channels | ForEach-Object { $channelCounts[$_.Name] = 0 }
$unmatched = 0

foreach ($r in $results) {
    $bestName = $null; $bestSim = 0
    foreach ($ch in $channels) {
        $dv = $r.Valence - $ch.Valence; $da = $r.Arousal - $ch.Arousal
        $sim = 1.0 - ([math]::Sqrt($dv*$dv + $da*$da) / [math]::Sqrt(2))
        if ($sim -gt $bestSim) { $bestSim = $sim; $bestName = $ch.Name }
    }
    if ($bestName -and $bestSim -gt 0.5) { $channelCounts[$bestName]++ } else { $unmatched++ }
}

foreach ($ch in $channels) {
    $count = $channelCounts[$ch.Name]; $pct = [math]::Round($count / $total * 100, 1)
    $bar = "#" * [math]::Min(50, [math]::Round($pct))
    Write-Host ("  {0,-12} {1,5} ({2,5:F1}%) {3}" -f $ch.Name, $count, $pct, $bar)
}
Write-Host ("  {0,-12} {1,5} ({2,5:F1}%)" -f "Unmatched", $unmatched, [math]::Round($unmatched/$total*100,1))

Write-Host ""
Write-Host "=== SAMPLE TRACKS ===" -ForegroundColor Yellow
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
