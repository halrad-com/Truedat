# Debug a few specific tracks step by step

$moods = Get-Content "C:\test\MB2\MusicBee\Library\mbxmoods.json" -Raw | ConvertFrom-Json

# Code defaults
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

# Pick tracks to debug
$debugTracks = @()
$i = 0
foreach ($prop in $moods.tracks.PSObject.Properties) {
    $t = $prop.Value
    # First 3 tracks, plus search for specific artists
    if ($i -lt 3 -or $t.artist -match "Ofege" -or $t.artist -match "She Keeps Bees" -or $t.artist -match "Dolly") {
        $hasRaw = ($t.spectralCentroid -gt 0) -or ($t.loudness -ne 0) -or ($t.danceability -gt 0)
        if ($hasRaw) {
            $debugTracks += @{ Path = $prop.Name; Track = $t }
        }
    }
    $i++
    if ($debugTracks.Count -ge 8) { break }
}

foreach ($dt in $debugTracks) {
    $t = $dt.Track
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "$($t.artist) - $($t.title) [$($t.genre)] mode=$($t.mode)" -ForegroundColor Cyan
    Write-Host "  Raw: bpm=$($t.bpm) cent=$($t.spectralCentroid) loud=$($t.loudness) flux=$($t.spectralFlux)"
    Write-Host "  Raw: dance=$($t.danceability) onset=$($t.onsetRate) zcr=$($t.zeroCrossingRate) rms=$($t.spectralRms)"
    Write-Host "  Raw: flat=$($t.spectralFlatness) diss=$($t.dissonance) sal=$($t.pitchSalience) chords=$($t.chordsChangesRate)"
    if ($t.mfcc -and $t.mfcc.Count -gt 1) { Write-Host "  Raw: mfcc[1]=$($t.mfcc[1])" }
    Write-Host ""

    # Valence components
    $modeScore = if ($t.mode -eq "major") { [double]$est.modeScoreMajor } else { [double]$est.modeScoreMinor }
    $centroidRange = [double]$est.centroidMax - [double]$est.centroidMin
    $centroidNorm = Clamp01(([double]$t.spectralCentroid - [double]$est.centroidMin) / $centroidRange)
    $danceNorm = Clamp01([double]$t.danceability / [double]$est.danceMax)
    $flatnessNorm = 1.0 - (Clamp01([double]$t.spectralFlatness))
    $dissonanceNorm = 1.0 - (Clamp01([double]$t.dissonance))
    $salienceNorm = Clamp01([double]$t.pitchSalience)
    $chordsNorm = Clamp01([double]$t.chordsChangesRate / [double]$est.chordsRateMax)
    $mfccNorm = 0.5
    $mfccRange = [double]$est.mfccMax - [double]$est.mfccMin
    if ($t.mfcc -and $t.mfcc.Count -gt 1 -and $mfccRange -gt 0) {
        $mfccNorm = Clamp01(([double]$t.mfcc[1] - [double]$est.mfccMin) / $mfccRange)
    }

    Write-Host "  VALENCE COMPONENTS:" -ForegroundColor Yellow
    Write-Host ("    mode={0}  score={1:F3}  * w={2:F2} = {3:F4}" -f $t.mode, $modeScore, $est.valenceWeightMode, ($est.valenceWeightMode * $modeScore))
    Write-Host ("    centroidNorm={0:F4}    * w={1:F2} = {2:F4}" -f $centroidNorm, $est.valenceWeightCentroid, ($est.valenceWeightCentroid * $centroidNorm))
    Write-Host ("    danceNorm={0:F4}       * w={1:F2} = {2:F4}" -f $danceNorm, $est.valenceWeightDance, ($est.valenceWeightDance * $danceNorm))
    Write-Host ("    flatnessNorm={0:F4}    * w={1:F2} = {2:F4}" -f $flatnessNorm, $est.valenceWeightFlatness, ($est.valenceWeightFlatness * $flatnessNorm))
    Write-Host ("    dissonanceNorm={0:F4}  * w={1:F2} = {2:F4}" -f $dissonanceNorm, $est.valenceWeightDissonance, ($est.valenceWeightDissonance * $dissonanceNorm))
    Write-Host ("    salienceNorm={0:F4}    * w={1:F2} = {2:F4}" -f $salienceNorm, $est.valenceWeightPitchSalience, ($est.valenceWeightPitchSalience * $salienceNorm))
    Write-Host ("    chordsNorm={0:F4}      * w={1:F2} = {2:F4}" -f $chordsNorm, $est.valenceWeightChords, ($est.valenceWeightChords * $chordsNorm))
    Write-Host ("    mfccNorm={0:F4}        * w={1:F2} = {2:F4}" -f $mfccNorm, $est.valenceWeightMfcc, ($est.valenceWeightMfcc * $mfccNorm))

    $valenceRaw = [double]$est.valenceWeightMode * $modeScore +
        [double]$est.valenceWeightCentroid * $centroidNorm +
        [double]$est.valenceWeightDance * $danceNorm +
        [double]$est.valenceWeightFlatness * $flatnessNorm +
        [double]$est.valenceWeightDissonance * $dissonanceNorm +
        [double]$est.valenceWeightPitchSalience * $salienceNorm +
        [double]$est.valenceWeightChords * $chordsNorm +
        [double]$est.valenceWeightMfcc * $mfccNorm
    $valence = Clamp01($valenceRaw)
    Write-Host ("    SUM (raw)={0:F4}  clamped={1:F4}" -f $valenceRaw, $valence) -ForegroundColor Green

    # Arousal components
    $bpmRange = [double]$est.bpmMax - [double]$est.bpmMin
    $bpmNorm = Clamp01(([double]$t.bpm - [double]$est.bpmMin) / $bpmRange)
    $loudRange = [double]$est.loudnessMax - [double]$est.loudnessMin
    $loudNorm = Clamp01(([double]$t.loudness - [double]$est.loudnessMin) / $loudRange)
    $fluxNorm = Clamp01([double]$t.spectralFlux / [double]$est.fluxMax)
    $danceNormA = Clamp01([double]$t.danceability / [double]$est.danceMax)
    $onsetNorm = Clamp01([double]$t.onsetRate / [double]$est.onsetRateMax)
    $zcrNorm = Clamp01([double]$t.zeroCrossingRate / [double]$est.zcrMax)
    $rmsNorm = Clamp01([double]$t.spectralRms / [double]$est.rmsMax)

    Write-Host ""
    Write-Host "  AROUSAL COMPONENTS:" -ForegroundColor Yellow
    Write-Host ("    bpmNorm={0:F4}         * w={1:F2} = {2:F4}" -f $bpmNorm, $est.arousalWeightBpm, ($est.arousalWeightBpm * $bpmNorm))
    Write-Host ("    loudNorm={0:F4}        * w={1:F2} = {2:F4}" -f $loudNorm, $est.arousalWeightLoudness, ($est.arousalWeightLoudness * $loudNorm))
    Write-Host ("    fluxNorm={0:F4}        * w={1:F2} = {2:F4}" -f $fluxNorm, $est.arousalWeightFlux, ($est.arousalWeightFlux * $fluxNorm))
    Write-Host ("    danceNorm={0:F4}       * w={1:F2} = {2:F4}" -f $danceNormA, $est.arousalWeightDance, ($est.arousalWeightDance * $danceNormA))
    Write-Host ("    onsetNorm={0:F4}       * w={1:F2} = {2:F4}" -f $onsetNorm, $est.arousalWeightOnsetRate, ($est.arousalWeightOnsetRate * $onsetNorm))
    Write-Host ("    zcrNorm={0:F4}         * w={1:F2} = {2:F4}" -f $zcrNorm, $est.arousalWeightZcr, ($est.arousalWeightZcr * $zcrNorm))
    Write-Host ("    rmsNorm={0:F4}         * w={1:F2} = {2:F4}" -f $rmsNorm, $est.arousalWeightRms, ($est.arousalWeightRms * $rmsNorm))

    $arousalRaw = [double]$est.arousalWeightBpm * $bpmNorm +
        [double]$est.arousalWeightLoudness * $loudNorm +
        [double]$est.arousalWeightFlux * $fluxNorm +
        [double]$est.arousalWeightDance * $danceNormA +
        [double]$est.arousalWeightOnsetRate * $onsetNorm +
        [double]$est.arousalWeightZcr * $zcrNorm +
        [double]$est.arousalWeightRms * $rmsNorm
    $arousal = Clamp01($arousalRaw)
    Write-Host ("    SUM (raw)={0:F4}  clamped={1:F4}" -f $arousalRaw, $arousal) -ForegroundColor Green
    Write-Host ""
}
