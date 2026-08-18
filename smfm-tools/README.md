# smfm-tools — Sony SMFM (12 TONE / SensMe) documentation & extraction tooling

Unofficial, reverse-engineered documentation of Sony's SMFM payload — the binary blob Sony's
music software writes to hold **12 TONE ANALYSIS / SensMe** results — plus the Python tools used
to extract and inspect it. Part technical reference, part compare/competitive analysis against
the open-source Essentia lineage truedat is built on.

**Not affiliated with or endorsed by Sony.** Everything here is inference from observed data,
with confidence tiers stated explicitly (see `SMFM-KNOWLEDGE.md`).

## What is SMFM?

`SMFM` is a four-byte identifier (`0x534D464D`) used as a FLAC APPLICATION block ID and as the
base name for sidecar files (`smfmf.bin`). It is written by `MMLib11.dll`
(**M**eta**M**usic **Lib**rary v11) and has carried Sony's 12 TONE / SensMe analysis results
across five hosts: SonicStage, x-APPLICATION, Media Go, VAIO MusicBox, and Music Center for PC.
Sony has never published a specification. Internal evidence (the `STAEMMLW` magic ≈ "Sound Tone
Analysis Engine — Music Markup Language Writer", the `ST*` sub-tag family) suggests **S**ound
**M**usic **F**eature **M**etadata — a working label until something authoritative surfaces.

"12 TONE" is the 12 chromatic pitch classes (chroma) — grounded in Sony patents US7601907B2 /
US7649137B2. SensMe is the mood layer built on top.

## Where it lives

The same payload bytes travel in four carriers:

| Carrier | Location |
| ------- | -------- |
| FLAC | `APPLICATION` metadata block, id `SMFM` |
| MP3 | ID3v2 `GEOB` frame, mime `application/SMFMF`, desc `USR_SMFMF` |
| WMA | ASF Extended Content Description, attribute `SMFMF` |
| anything else | sidecar `%APPDATA%\Sony\Music Center\fringe\audio\<pfx>\<id>\smfmf.bin` |

Music Center's `tracks.db` (a JSONL append log in `%APPDATA%\Sony\Music Center\db\`) flags
analyzed tracks with `{"twelveTone": 1}` and points at sidecars.

## How truedat uses it

truedat reads SMFM if present (header-only, refreshed every scan, never cached) and emits into
`mbxmoods.json`: `smfmScores` (the 10 raw mood-model scores, int 0–255), `smfmChannel` (argmax
slot — **not** a SensMe channel; see below), `smfmChannelName` (always null — the slot→name idea
was refuted by device testing), and `smfmBpm` (Sony's GBPM). Legacy `sensme*` keys are still
readable; `truedat --migrate` renames them in place.

## Docs

| Doc | What |
| --- | ---- |
| [`SMFM-FORMAT.md`](SMFM-FORMAT.md) | The wire format: TLV layout, sub-block table with confidence tiers, hex examples, open questions |
| [`SMFM-KNOWLEDGE.md`](SMFM-KNOWLEDGE.md) | State of knowledge: KNOWN / PARTIAL / HYPOTHESIS / OPEN / **FALSIFIED** — the canonical record, including the claims that were disproven |
| [`SMFM-VS-ESSENTIA.md`](SMFM-VS-ESSENTIA.md) | The SWOT: SMFM as a mood source compared against Essentia extraction, every claim evidence-tagged |

The headline finding, so nobody re-derives it the hard way: **the 10 STMO slots are mood-model
scores, not SensMe channels.** Device SensMe channels are overlapping regions on a 2-D
arousal × valence canvas; no slot→channel mapping exists (a live 52-track Walkman test refuted
it). And the slots are whole-track values — the apparent 4× "segments" are byte-identical
replicas, not a temporal arc.

## Tools

Python 3.9+, standard library only.

| Script | Purpose |
| ------ | ------- |
| `smfm_export.py` | Decode a whole library → portable JSON. tracks.db mode (follows `twelveTone=1`, reads embedded blocks + sidecars) or `--scan <root>` folder mode (devices, shares — no tracks.db needed) |
| `smfm_index.py` | Walk tracks.db → diagnostic CSV, one row per analyzed track with all decoded fields |
| `flac_smfm_dump.py` | Hex-dump the SMFM block from a single FLAC, list candidate sub-tags |
| `flac_smfm_diff.py` | Sub-block-by-sub-block diff of two FLAC SMFM payloads |
| `check_moods_smfm.py` | SMFM coverage report against a truedat `mbxmoods.json`; writes both directions — `smfm-present.csv` (what HAS it, with the decoded values) and `smfm-missing.csv` (Sony-tagging candidates) |
| `smfm_strip_copy.py` | Copy a FLAC/MP3 **without** its SMFM block. Never modifies the source; audio frames are copied byte-for-byte, so the copy keeps the same `audioStreamSha256` and truedat treats it as the same track. `--self-test` runs synthetic-container regressions |

## Provenance & confidence

Reverse-engineered from observed data: 900+ analyzed tracks across all four carriers, raw-byte
verification runs (e.g. the STMO sub-index result covers 362 tracks at 100%), and a live
52-track device test on a Walkman. Where a claim is a guess, the docs say so; where an earlier
claim was disproven, it is listed under FALSIFIED in `SMFM-KNOWLEDGE.md` rather than silently
removed. Sony product names (SensMe, 12 TONE, ZAPPIN, Music Center) are used descriptively to
identify what is being documented.
