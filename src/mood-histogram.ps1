# mood-histogram.ps1 - Simple bucket histogram of valence/arousal with code defaults

param(
    [string]$MoodsFile = "C:\test\MB2\MusicBee\Library\mbxmoods.json"
)

$moods = Get-Content $MoodsFile -Raw | ConvertFrom-Json

$est = @{
    bpmMin = 80; bpmMax = 170
    centroidMin = 400.0; centroidMax = 2500.0
    loudnessMin = -25.0; loudnessMax = -5.0
    onsetRateMax = 6.0; zcrMax = 0.15; rmsMax = 0.01
    chordsRateMax = 0.2; fluxMax = 0.15; danceMax = 2.0
    mfccMin = 50.0; mfccMax = 250.0
    modeScoreMajor = 0.8; modeScoreMinor = 0.4
    valenceWeightMode = 0.25; valenceWeightCentroid = 0.15; valenceWeightDance = 0.10
    valenceWeightFlatness = 0.0; valenceWeightDissonance = 0.20
    valenceWeightPitchSalience = 0.15; valenceWeightChords = 0.05; valenceWeightMfcc = 0.10
    arousalWeightBpm = 0.20; arousalWeightLoudness = 0.15; arousalWeightFlux = 0.15
    arousalWeightDance = 0.10; arousalWeightOnsetRate = 0.15; arousalWeightZcr = 0.10
    arousalWeightRms = 0.15
}

function Clamp01([double]$v) { [math]::Max(0.0, [math]::Min(1.0, $v)) }

# 10 buckets: 0.0-0.1, 0.1-0.2, ..., 0.9-1.0
$vBuckets = @(0,0,0,0,0,0,0,0,0,0)
$aBuckets = @(0,0,0,0,0,0,0,0,0,0)
$vSum = 0.0; $aSum = 0.0; $vMin = 1.0; $vMax = 0.0; $aMin = 1.0; $aMax = 0.0
$total = 0

# Channel counters
$chNames = @("Energetic","Dance","Intense","Upbeat","Morning","Chill","Mellow","Night","Emotional","Relax")
$chA = @(0.90, 0.85, 0.75, 0.70, 0.55, 0.35, 0.25, 0.20, 0.40, 0.15)
$chV = @(0.80, 0.75, 0.35, 0.80, 0.75, 0.65, 0.55, 0.35, 0.30, 0.60)
$chCount = @(0,0,0,0,0,0,0,0,0,0)
$unmatched = 0

foreach ($prop in $moods.tracks.PSObject.Properties) {
    $t = $prop.Value
    $hasRaw = ($t.spectralCentroid -gt 0) -or ($t.loudness -ne 0) -or ($t.danceability -gt 0)
    if (-not $hasRaw) { continue }

    # Valence
    $modeScore = if ($t.mode -eq "major") { 0.8 } else { 0.4 }
    $centroidNorm = Clamp01(([double]$t.spectralCentroid - 400.0) / 2100.0)
    $danceNorm = Clamp01([double]$t.danceability / 2.0)
    $dissonanceNorm = 1.0 - (Clamp01([double]$t.dissonance))
    $salienceNorm = Clamp01([double]$t.pitchSalience)
    $chordsNorm = Clamp01([double]$t.chordsChangesRate / 0.2)
    $mfccNorm = 0.5
    if ($t.mfcc -and $t.mfcc.Count -gt 1) {
        $mfccNorm = Clamp01(([double]$t.mfcc[1] - 50.0) / 200.0)
    }

    $v = Clamp01(0.25 * $modeScore + 0.15 * $centroidNorm + 0.10 * $danceNorm +
        0.20 * $dissonanceNorm + 0.15 * $salienceNorm + 0.05 * $chordsNorm + 0.10 * $mfccNorm)

    # Arousal
    $bpmNorm = Clamp01(([double]$t.bpm - 80.0) / 90.0)
    $loudNorm = Clamp01(([double]$t.loudness - (-25.0)) / 20.0)
    $fluxNorm = Clamp01([double]$t.spectralFlux / 0.15)
    $danceNormA = Clamp01([double]$t.danceability / 2.0)
    $onsetNorm = Clamp01([double]$t.onsetRate / 6.0)
    $zcrNorm = Clamp01([double]$t.zeroCrossingRate / 0.15)
    $rmsNorm = Clamp01([double]$t.spectralRms / 0.01)

    $a = Clamp01(0.20 * $bpmNorm + 0.15 * $loudNorm + 0.15 * $fluxNorm +
        0.10 * $danceNormA + 0.15 * $onsetNorm + 0.10 * $zcrNorm + 0.15 * $rmsNorm)

    # Bucket
    $vBucket = [math]::Min(9, [math]::Floor($v * 10))
    $aBucket = [math]::Min(9, [math]::Floor($a * 10))
    $vBuckets[$vBucket]++
    $aBuckets[$aBucket]++
    $vSum += $v; $aSum += $a
    if ($v -lt $vMin) { $vMin = $v }; if ($v -gt $vMax) { $vMax = $v }
    if ($a -lt $aMin) { $aMin = $a }; if ($a -gt $aMax) { $aMax = $a }
    $total++

    # Channel assignment
    $bestIdx = -1; $bestSim = 0
    for ($ci = 0; $ci -lt 10; $ci++) {
        $dv = $v - $chV[$ci]; $da = $a - $chA[$ci]
        $sim = 1.0 - ([math]::Sqrt($dv*$dv + $da*$da) / 1.4142)
        if ($sim -gt $bestSim) { $bestSim = $sim; $bestIdx = $ci }
    }
    if ($bestIdx -ge 0 -and $bestSim -gt 0.5) { $chCount[$bestIdx]++ } else { $unmatched++ }
}

Write-Host "=== MOOD HISTOGRAM (code defaults, $total tracks) ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "VALENCE  min=$([math]::Round($vMin,3)) max=$([math]::Round($vMax,3)) avg=$([math]::Round($vSum/$total,3))" -ForegroundColor Yellow
for ($i = 0; $i -lt 10; $i++) {
    $lo = $i / 10.0; $hi = ($i + 1) / 10.0
    $pct = [math]::Round($vBuckets[$i] / $total * 100, 1)
    $bar = "#" * [math]::Min(60, [math]::Round($pct * 1.2))
    Write-Host ("  [{0:F1}-{1:F1})  {2,5} ({3,5:F1}%) {4}" -f $lo, $hi, $vBuckets[$i], $pct, $bar)
}

Write-Host ""
Write-Host "AROUSAL  min=$([math]::Round($aMin,3)) max=$([math]::Round($aMax,3)) avg=$([math]::Round($aSum/$total,3))" -ForegroundColor Yellow
for ($i = 0; $i -lt 10; $i++) {
    $lo = $i / 10.0; $hi = ($i + 1) / 10.0
    $pct = [math]::Round($aBuckets[$i] / $total * 100, 1)
    $bar = "#" * [math]::Min(60, [math]::Round($pct * 1.2))
    Write-Host ("  [{0:F1}-{1:F1})  {2,5} ({3,5:F1}%) {4}" -f $lo, $hi, $aBuckets[$i], $pct, $bar)
}

Write-Host ""
Write-Host "MOOD CHANNELS:" -ForegroundColor Yellow
for ($i = 0; $i -lt 10; $i++) {
    $pct = [math]::Round($chCount[$i] / $total * 100, 1)
    $bar = "#" * [math]::Min(50, [math]::Round($pct))
    Write-Host ("  {0,-12} {1,5} ({2,5:F1}%) {3}" -f $chNames[$i], $chCount[$i], $pct, $bar)
}
Write-Host ("  {0,-12} {1,5} ({2,5:F1}%)" -f "Unmatched", $unmatched, [math]::Round($unmatched/$total*100,1))
