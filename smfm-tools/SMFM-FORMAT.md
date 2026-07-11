# SMFM — Sony 12 TONE ANALYSIS Wire Format (unofficial)

> **Unofficial, reverse-engineered documentation. Not a Sony specification.**
> Canonical state of knowledge (known / open / falsified): **`SMFM-KNOWLEDGE.md`** — when this
> file and that one disagree, that one wins.

Notes on the binary payload Sony uses to store 12 TONE / SensMe analysis results across
SonicStage, x-APPLICATION, Media Go, VAIO MusicBox and Music Center for PC.

**Status: working draft.** Container layout is confirmed. Sub-block payload semantics range from
confirmed (`GBPM`) to hypothesis — each row in the table below carries its confidence tier.

## Provenance

- The same payload bytes travel in four carriers (all confirmed by observation):
  1. **FLAC** — `APPLICATION` block with 4-char ID `SMFM` (`0x534D464D`). Sony has **not**
     registered this ID with IANA (only `SONY` / `0x534F4E59` for Sony Creative Software is
     registered against RFC 9639).
  2. **MP3** — ID3v2 `GEOB` frame, mime `application/SMFMF`, description `USR_SMFMF`.
  3. **WMA** — ASF Extended Content Description, attribute name `SMFMF`.
  4. **Sidecar** — for containers where embedding is awkward (e.g. WAV), file
     `%APPDATA%\Sony\Music Center\fringe\audio\<2-char prefix>\<id+base64>\smfmf.bin`,
     referenced by the `smfmf` field in the track's `tracks.db` JSON record.
- Written by `MMLib11.dll` (**M**eta**M**usic **Lib**rary v11) shipped in
  `C:\Program Files (x86)\Sony\Music Center\AVLib\`.
- The constant 8-byte magic inside every sub-block is `STAEMMLW` for PC-written payloads and
  `STAESMML` for Walkman-written ones (same TLV layout either way). `STAEMMLW` is almost
  certainly "**S**ound **T**one **A**nalysis **E**ngine — **M**usic **M**arkup **L**anguage
  **W**riter". The `SMFM` expansion itself is inference (working hypothesis: **S**ound **M**usic
  **F**eature **M**etadata) — treat it as a working label until something authoritative surfaces.

## Container layout

The SMFM payload is a flat sequence of TLV sub-blocks with a fixed 20-byte header each.

```
Per sub-block header (20 bytes):
  +0  4 bytes  : sub-tag FOURCC                  (GBPM, STBF, STSA, STMO, STHF, STMM, ...)
  +4  8 bytes  : "STAEMMLW" / "STAESMML"         (ASCII magic)
  +12 4 bytes  : flags / version                 (always 01 00 80 00 in observed data)
  +16 4 bytes  : payload length N                (big-endian uint32)
  +20 N bytes  : payload
```

Sub-blocks are written back-to-back; no padding between them. The six common sub-blocks below
sum exactly to the FLAC APPLICATION payload size and the `smfmf.bin` file size, in every sample
observed.

## Sub-blocks (observed across 900+ analyzed tracks)

| Tag    | Typical Len | Confidence | Meaning |
| ------ | ----------- | ---------- | ------- |
| `GBPM` | 4           | confirmed  | **G**lobal **BPM** — float32 big-endian |
| `STMO` | 160         | confirmed (structure) | 10 whole-track mood-model scores, replicated 4× (sub-indices NOT temporal — see below). Slots do **not** map 1:1 to SensMe channels. |
| `STBF` | 36          | partial    | NOT a 12-tone chroma (hypothesis falsified 2026-06-27). Fixed-axis spectral feature vector: 2 header scalars + 33-value body + `0xff` sentinel at offset 28. Largely redundant with open-source spectral descriptors (R² up to 0.77). |
| `STHF` | 160         | partial    | 40-bin loudness/energy curve over the track — the only within-track temporal signal in SMFM. Structure decoded; exact unit/meaning partial. |
| `STSA` | 8           | hypothesis | Two big-endian uint32 — highlight (ZAPPIN) in/out point, likely centiseconds |
| `STMM` | 2700–4000   | hypothesis | Beat grid / segment timeline. Dense stream of small records; the global BPM float reappears within at offset 0x208 |
| `STNM` | ?           | undecoded  | Present in Msc.dll's tag list; never captured/decoded |
| `GVNM` | ?           | undecoded  | Present in Msc.dll's tag list; never captured/decoded |

### GBPM — confirmed

```
47 42 50 4d                   "GBPM"
53 54 41 45 4d 4d 4c 57       "STAEMMLW"
01 00 80 00                   flags
00 00 00 04                   length = 4 (BE)
42 ae 12 b0                   payload = float32 BE = 87.034 (BPM)
```

Sleepy Lagoon (Vaughn Monroe, 1942, slow swing): 87.034 BPM — plausible.
The Old Man 4416 (BATIK jazz): 92.193 BPM — plausible.

### STMO — mood-model scores

160 bytes = 40 × 4-byte entries of `01 SL SI VV`:

- `01` — constant tag/type byte
- `SL` — slot index 0–9 (10 distinct values observed)
- `SI` — sub-index 0–3. **NOT temporal.** Raw-byte-verified across 362 tracks: the 4 sub-indices
  are IDENTICAL in 100% of cases. The earlier "per-segment / per-quarter" reading is
  **falsified** — STMO is a single whole-track value replicated 4×; averaging the replicas is a
  no-op. There is no within-track mood arc in STMO (that lives in STHF, if anywhere).
- `VV` — score byte 0–255. `0xFE` (254) appears as a degenerate/junk value on some tracks
  (observed on low-quality live recordings) — "analyzed" does not imply "usable".

Example (Sleepy Lagoon), decoded slot scores: `[49, 37, 45, 47, 24, 40, 0, 55, 24, 61]`.

The 10 slots are **per-slot scores over Sony's internal mood model** — they do **not** map 1:1
to the SensMe channel names shown on devices. A live device test established that SensMe
channels are overlapping *regions on a 2-D arousal × valence canvas*, not bins of these slots;
the slot argmax does not predict the device channel. Details and the falsified earlier
slot→channel tables: `SMFM-KNOWLEDGE.md`.

### STHF — 40-bin energy curve

160 bytes = 40 × 4-byte entries of `08 II VV 00`:

- `08` — constant
- `II` — bin index 0–39
- `VV` — value (observed 0x1D–0x4E range)
- `00` — constant

Looks like a thumbnail / preview waveform sampled across the whole track. For a 3-minute track,
each bin spans ~4.5 seconds. This is the only within-track temporal signal in the payload.

### STMM — beat/segment timeline (largest block)

3000–4000 bytes typically. Starts with a header that includes the global BPM float (same 4 bytes
as the GBPM payload) at offset 0x208 within the block. Followed by a dense stream of small
records (~9 bytes each) with monotonically increasing timecodes. Likely the beat grid and
segment boundaries used to align ZAPPIN and SensMe playback.

## How Music Center for PC uses it

- **`tracks.db`** (`%APPDATA%\Sony\Music Center\db\tracks.db`) is a JSONL append log. Each track
  gets a record with file metadata, IDs, and an optional `analysis` block.
- Analysis flags: `{"twelveTone": 1}` means SMFM data is reachable for the track.
  `gracenote: 1/-1` is a separate flag for online text-metadata lookup (Gracenote supplies no
  audio analysis — see `SMFM-KNOWLEDGE.md`).
- For files that already contained SMFM (legacy rips), Music Center reads it in place and just
  sets the flag. No file modification.
- For newly-analyzed files, Music Center writes `fringe/audio/<prefix>/<id>/smfmf.bin` and adds
  `"smfmf": "<path>"` to the record. **Source files are not modified** during library import —
  a folder watcher observed zero source-file writes across import and analysis runs.

## Tools in this directory

- `flac_smfm_dump.py <file.flac>` — walk FLAC metadata blocks, hex-dump the SMFM payload, list
  candidate sub-tag tokens.
- `flac_smfm_diff.py <a.flac> <b.flac>` — diff two SMFM payloads sub-block by sub-block.
- `smfm_export.py` — decode a whole library (tracks.db mode or folder-scan mode) to portable JSON.
- `smfm_index.py` — walk `tracks.db`, produce a diagnostic CSV with decoded fields per track.
- `check_moods_smfm.py` — SMFM coverage report against a truedat `mbxmoods.json`.

## Open questions

1. **STBF body semantics** — 36 bytes = 2 header scalars (offset 0: 0–22 bell; offset 1: 50–207)
   + 33-value fixed-axis body + constant `0xff` at offset 28. Body argmax pins to fixed offsets
   21/23 regardless of musical key (not transposable) ⇒ fixed-axis features, not pitch classes.
   Remaining work: regress the body values against open-source descriptors to assign each offset
   a meaning (only the highest-R² offsets are characterized so far).
2. **STMM record layout** — count records and confirm they're roughly `(duration_s × BPM / 60)`
   apart; that would prove the beat-grid hypothesis.
3. **Flags `01 00 80 00`** — likely a version/type bitfield. Check whether older Media Go /
   x-APPLICATION rips carry a different value.
4. **STNM / GVNM** — capture and decode.
