# poke-metaserver.ps1 — minimal contract-compliant POST to MetaServer /meta/ingest.
# Removes truedat from the equation: hand-built payload, single round-trip,
# prints status + body. If this gets 200, truedat is the problem. If this gets
# 4xx/5xx, MetaServer is the problem. Either way we know in 30 seconds.
#
# Usage:
#   pwsh -File tools/poke-metaserver.ps1 -MetaServer http://localhost:8081
#   pwsh -File tools/poke-metaserver.ps1 -MetaServer http://localhost:8081 -Path "n:\some\real\file.flac"
#
# The default payload is the cheap-fingerprint shape from
# docs/reference/identity-wire-format.md §1.

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$MetaServer,

    [string]$Path = "D:\Music\TestArtist\TestAlbum\01 - Test.flac",

    [switch]$IncludeAudioStreamSha256,

    [switch]$DefaultEssentiaShape  # post a default-mode-shaped payload (with features) instead of hash-only shape
)

$ErrorActionPreference = "Stop"

$url = ($MetaServer.TrimEnd('/')) + "/meta/ingest"

# pathTail per contract §pathTail: last 3 segments, lowercased, '\\' separator.
function Get-PathTail([string]$p) {
    $segs = ($p -replace '/', '\').Split('\') | Where-Object { $_ -ne '' }
    if ($segs.Count -lt 2) { return $null }
    $take = [Math]::Min(3, $segs.Count)
    return ($segs[($segs.Count - $take)..($segs.Count - 1)] -join '\').ToLowerInvariant()
}

$tail = Get-PathTail $Path
if (-not $tail) { Write-Error "Path needs at least 2 segments for pathTail."; exit 2 }

$identity = [ordered]@{
    "fingerprint.v1" = [ordered]@{
        fileSize        = 41234567
        pathTail        = $tail
        durationMs      = 246000
        sampleRate      = 44100
        channels        = 2
        codec           = "flac"
        bitrate         = 1024
        audioHead64kMd5 = "c2f3b9d5a7e04815bce09f2318c18a10"
    }
}
if ($IncludeAudioStreamSha256) {
    $identity.audioStreamSha256 = "a4b9e2f1c8d6e5f1234ab00a4b9e2f1c8d6e5f1234ab00a4b9e2f1c8d6e5f123"
}

if ($DefaultEssentiaShape) {
    # Add the bits PostToMetaServer would add in default mode.
    $identity.fileMd5 = "c2f3b9d5a7e04815bce09f2318c18a10"
    $payload = [ordered]@{
        path     = $Path
        metadata = [ordered]@{
            artist   = "TestArtist"
            title    = "Test"
            album    = "TestAlbum"
            genre    = "Test"
            duration = 246.0
            fileSize = 41234567
        }
        identity = $identity
        features = [ordered]@{
            bpm                = 128.3
            key                = "A"
            mode               = "minor"
            spectralCentroid   = 2500.0
            spectralFlux       = 0.3
            loudness           = -12.5
            danceability       = 1.2
            onsetRate          = 3.5
            zeroCrossingRate   = 0.08
            spectralRms        = 0.15
            spectralFlatness   = 0.05
            dissonance         = 0.45
            pitchSalience      = 0.6
            chordsChangesRate  = 0.2
            mfcc               = @(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0)
        }
        provenance = [ordered]@{
            scannedBy   = $env:COMPUTERNAME
            tool        = "essentia"
            toolVersion = "0.0.0-poke"
            scannedAt   = (Get-Date).ToUniversalTime().ToString("o")
        }
    }
} else {
    $payload = [ordered]@{
        path     = $Path
        metadata = [ordered]@{
            duration = 246.0
            fileSize = 41234567
        }
        identity = $identity
        provenance = [ordered]@{
            scannedBy   = $env:COMPUTERNAME
            tool        = "truedat-hash"
            toolVersion = "0.0.0-poke"
            scannedAt   = (Get-Date).ToUniversalTime().ToString("o")
            level       = "fingerprint"
        }
    }
}

$json = $payload | ConvertTo-Json -Depth 10 -Compress

$shape = if ($DefaultEssentiaShape) { "default-essentia" } else { "hash-only fingerprint" }
$sha   = if ($IncludeAudioStreamSha256) { "yes" } else { "no" }
Write-Host "POST $url"
Write-Host "  shape: $shape"
Write-Host "  audioStreamSha256: $sha"
Write-Host "  body bytes: $($json.Length)"
Write-Host ""
Write-Host "--- request body ---"
Write-Host ($payload | ConvertTo-Json -Depth 10)
Write-Host ""
Write-Host "--- response ---"

try {
    $response = Invoke-WebRequest -Uri $url -Method POST -Body $json -ContentType "application/json" -UseBasicParsing -ErrorAction Stop
    Write-Host "STATUS: $([int]$response.StatusCode) $($response.StatusDescription)"
    Write-Host "BODY:"
    Write-Host $response.Content
    exit 0
} catch [System.Net.WebException] {
    $resp = $_.Exception.Response
    if ($resp) {
        $code = [int]$resp.StatusCode
        $desc = $resp.StatusDescription
        $stream = $resp.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($stream)
        $body = $reader.ReadToEnd()
        Write-Host "STATUS: $code $desc"
        Write-Host "BODY:"
        Write-Host $body
        exit 1
    } else {
        Write-Host "TRANSPORT ERROR: $($_.Exception.Message)"
        exit 2
    }
} catch {
    # PS 7+ wraps non-2xx in HttpResponseException
    if ($_.Exception.Response) {
        $code = [int]$_.Exception.Response.StatusCode
        $desc = $_.Exception.Response.ReasonPhrase
        $body = ""
        try { $body = $_.ErrorDetails.Message } catch {}
        if (-not $body -and $_.Exception.Response.Content) {
            try { $body = $_.Exception.Response.Content.ReadAsStringAsync().GetAwaiter().GetResult() } catch {}
        }
        Write-Host "STATUS: $code $desc"
        Write-Host "BODY:"
        Write-Host $body
        exit 1
    } else {
        Write-Host "ERROR: $($_.Exception.Message)"
        exit 2
    }
}
