# DSD/Non-PCM Format Conversion Plan

## Status: Parked

## Problem

Essentia tools and chromaprint/md5 only accept PCM audio. DSD formats (DSF, DFF) fail silently or with unhelpful errors. ffprobe (`--details`) already handles these fine.

## Proposed Solution

New flag: `--convert` (or `--transcode`) — enables ffmpeg-based conversion of non-PCM formats to PCM WAV before analysis/fingerprinting.

- Off by default — no behavior change for existing users
- When enabled, detect by file extension and convert preemptively (no wasted tool attempts)
- Applies to analysis and fingerprint modes, not details (ffprobe handles all formats natively)

## Target Extensions

| Extension | Format | Notes |
|-----------|--------|-------|
| `.dsf` | DSD Stream File | Most common DSD format |
| `.dff` | DSDIFF | Older DSD format |

## Conversion Approach

```
ffmpeg -i input.dsf -ac 2 -ar 44100 -sample_fmt s16 -y output.wav
```

- Downsample to 44.1kHz/16-bit stereo — Essentia features and chromaprint don't benefit from hi-res
- Stereo output handles both stereo and multi-channel DSD in one step (no separate downmix retry needed)
- Temp WAV written to system temp directory, deleted after tool completes

## Implementation

### Detection

Extension-based, checked early before tool invocation:

```csharp
static readonly HashSet<string> _convertExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".dsf", ".dff"
};

static bool NeedsConversion(string audioPath)
{
    return _convertExtensions.Contains(Path.GetExtension(audioPath));
}
```

### Conversion Flow

Reuse the `DownmixToStereo` pattern but with different ffmpeg args:

```csharp
static string? ConvertToPcm(string audioPath)
{
    // ffmpeg -i input -ac 2 -ar 44100 -sample_fmt s16 -y output.wav
    // Returns temp WAV path on success, null on failure
    // Caller deletes temp file
}
```

### Integration Points

1. **AnalyzeWithEssentia** — if `NeedsConversion(audioPath)`, convert first, pass temp WAV to `AnalyzeWithEssentiaCore`. This replaces the current flow where multi-channel retry happens after failure. When `--convert` is active and the file needs conversion, the converted WAV is already stereo PCM so the multi-channel retry path is skipped entirely.

2. **RunChromaprinter / RunMd5** — same pattern. Convert before calling `RunTool`. Again, converted file is already stereo so multi-channel retry is unnecessary.

3. **ProbeAudio** — no conversion needed. ffprobe reads all formats.

### Optimization When `--convert` Is Active

- Pre-convert once, reuse the temp WAV across all tools for the same track
- In `--all` mode (fingerprint + details + analysis), a single conversion serves both fingerprint and analysis passes
- Log conversion: `DEBUG convert: {sizeMb:F1} MB DSF -> {tempMb:F1} MB WAV ({elapsed:F1}s)`

### Temp File Management

- Same pattern as existing downmix: write to `Path.GetTempPath()`, prefix `truedat_pcm_`
- Delete in `finally` block after all tools complete for that track
- Orphan cleanup in `CleanupOrphanedFiles` (add `truedat_pcm_*.wav` pattern)

## Audit Logging (--audit)

All conversion activity must be visible in the audit log:

- `DEBUG convert: {ext} detected, converting: {audioPath}`
- `DEBUG convert: {srcMb:F1} MB {ext} -> {tmpMb:F1} MB WAV ({elapsed:F1}s): {audioPath}`
- `DEBUG convert: failed (exit {exitCode}): {audioPath}` with stderr snippet
- `DEBUG convert: timeout after {seconds}s: {audioPath}`
- `DEBUG convert: cleanup {tempPath}` on delete
- `DEBUG convert: cleanup failed: {tempPath}: {ex.Message}` if delete fails

Conversion failures go to the errors CSV with a clear reason (e.g. `DSD conversion failed (exit 1)`).

## Temp File Tracking

- Every temp file creation is logged when `--audit` is active
- Every temp file deletion is logged (success or failure)
- Orphan cleanup at startup logs count and paths of stale `truedat_pcm_*.wav` files
- If a temp file can't be deleted, log a WARNING (not just swallow the exception)

## Output

No schema changes. Features, fingerprints, and details are identical regardless of source format. The conversion is transparent to downstream consumers.

## Considerations

- DSD files can be 200-500 MB per track. Temp WAV at 44.1kHz/16-bit stereo is ~10 MB/min, so a 5-minute track ≈ 50 MB temp. Acceptable.
- Conversion adds ~5-10s per track depending on DSD rate and disk speed
- Large temp files with high parallelism: 24 threads × 50 MB = ~1.2 GB peak temp usage. Fine for most systems.
- Could extend `_convertExtensions` later for other exotic formats without code changes
