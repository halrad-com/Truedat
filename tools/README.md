# tools/

Scripts and helpers that sit alongside the truedat exe but ship outside it.

## poke-metaserver.ps1

Minimal contract-compliant POST to `/meta/ingest`. Removes truedat from the
equation when triaging "is the wire payload right?" questions. See the script
header for usage.

## verify-audiosha-determinism.ps1

Cross-machine sanity rig for `audioStreamSha256`. Verifies that two machines
holding the same audio bytes produce the same `audioStreamSha256`. Required by
the Phase 2 / Layer 4 spec §3.2 ("determinism across machines") before MBXHub
can promote `audioStreamSha256` to a primary identity in `TrackLocations`.

### How it works

The script enumerates audio files under a directory, writes a temp file-list,
and runs:

```text
truedat.exe --hash-only --level stream --file-list <tmp> --output <manifest>
```

The new `--output <path>` flag (added alongside the script) makes truedat
append one identity envelope per file as NDJSON to `<manifest>`. No HTTP
server required — the rig is fully offline.

Each NDJSON line is the same envelope shape `PostIdentityOnly` would POST
to `/meta/ingest`, including `identity.audioStreamSha256` and (when the
TagLib invariant region was unavailable) `identity.audioStreamSha256Source`.

### Two-machine verification protocol

1. **Pick a small representative directory.** ~20 files mixing FLAC, MP3, M4A,
   OGG. Include some edge cases: a file with embedded cover art, a file with
   extensive ID3v2 tags, an untagged WAV, a FLAC with custom Vorbis comments.
2. **Copy that directory byte-for-byte to both machines.** Use `robocopy /MIR`
   or equivalent. Verify with `Get-FileHash` after copy that file MD5s match
   on both sides — this rules out copy corruption masquerading as hash drift.
3. **Run the rig on each machine:**

   ```powershell
   pwsh -File tools/verify-audiosha-determinism.ps1 `
       -InputDir D:\test-corpus `
       -OutputManifest C:\out\manifest-machineA.ndjson
   ```

4. **Diff the manifests.** Every `audioStreamSha256` should match. Every
   `audioStreamSha256Source` should also match — if one machine falls back to
   `"whole-file"` and the other doesn't, that's a TagLib environmental
   difference worth investigating before declaring determinism verified.
5. **Tag-edit invariance check.** Edit a tag on one machine (e.g. change a
   title in MP3Tag), re-run the rig on that machine, and diff the new
   manifest against the original from the other machine:
   - Files where `audioStreamSha256Source` is `"invariant"` (the omit-default)
     should still match — the audio region didn't change.
   - Files where `audioStreamSha256Source` is `"whole-file"` will diverge for
     the edited file. This is the pass criterion: the source signal is
     meaningful and consumers can trust it.

### Exit codes

The script exits 0 on success and 1 if truedat returns non-zero or the manifest
isn't produced. Per-file failures are written to `mbxhub-hash-only-errors.csv`
adjacent to the file-list (truedat's existing convention).
