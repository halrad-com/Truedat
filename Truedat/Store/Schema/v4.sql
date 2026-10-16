-- truedat store — schema v4 (2026-10-16, store v3 sprint T05 + T19). Forward-only; applied on top of v3 by SchemaMigrator inside
-- one transaction, after a VACUUM INTO backup of the v3 file; SchemaV4Migration.RunDataStep moves every packed edge.row value
-- into the typed edge table (keeping its arrays: a v3 store captured them, and capture is sticky per track) and derives the
-- verdict projection for every location.

-- ---------------------------------------------------------------- the edge, typed (T05)
-- One row per edge (head | tail) per content per producer version. The DEFAULT row is the scalars the readers use plus the
-- edge's whole-summary HPCP (operator, 2026-10-15: "opt in for the arrays. just keep the minimal mvp by default" / "keep HPCP
-- in the default"). Values are the fold's float32 widened to REAL — no rounding is added on the way in. The opt-in capture
-- (series, beats, sub-window summaries, MFCC/spectral vectors) rides in `arrays` as the existing edge-row/1 blob, named by
-- `arrays_dtype`; a track whose row holds arrays keeps them on every later write ("sticky per track. keep what we captured"),
-- and only the explicit discard verb clears them.
CREATE TABLE edge (
  content_hash      TEXT NOT NULL,
  content_hash_kind TEXT NOT NULL,
  producer_version  TEXT NOT NULL,
  kind              TEXT NOT NULL CHECK (kind IN ('head','tail')),
  run_id            INTEGER REFERENCES run(id),
  status            TEXT NOT NULL CHECK (status IN ('ok','short-context')),
  start_ms          INTEGER NOT NULL,
  end_ms            INTEGER NOT NULL,
  key_name TEXT, key_scale TEXT, key_code TEXT, key_strength REAL, key_agreement INTEGER, key_votes INTEGER,
  bpm REAL, bpm_beats INTEGER, bpm_regularity REAL,
  start_lufs REAL, end_lufs REAL, mean_lufs REAL, fade_ms INTEGER, silence_ms INTEGER, silence_source TEXT,
  crest_db REAL, transition_ms INTEGER,
  div_loudness REAL, div_texture REAL, div_harmony REAL,
  centroid REAL, dissonance REAL,       -- the edge's spectral centroid / dissonance means (the readers' regional terms)
  hpcp BLOB,                            -- 36 × float32 LE: the edge's whole-summary HPCP, the fold's own precision
  arrays BLOB,                          -- opt-in: the edge-row/1 blob (EdgeStore.PackEdge); NULL = default capture
  arrays_dtype TEXT,                    -- 'edge-row/1' when arrays is set, else NULL
  updated_utc TEXT NOT NULL,
  PRIMARY KEY (content_hash, producer_version, kind)
) WITHOUT ROWID;

-- The value row gains the producer's element type for its vector (DBA review F6.1): 'f32' (float32 LE) today.
ALTER TABLE value ADD COLUMN dtype TEXT;

-- ---------------------------------------------------------------- the verdict projection, per copy (T19)
-- The catalog's `truedat` block, computed by truedat's own ComputeTruedatVerdict from the row AS STORED, so a reader of the
-- store sees exactly what the exported catalog says. Keyed by LOCATION: the verdict reads the copy's facts (codec, bit depth,
-- sample rate, LAME tag) as well as the media's description, so two copies of one audio can legitimately differ. A row exists
-- iff the catalog writer emits the block; absent row == absent block. Confidences are REAL (the writer emits them shortest
-- round-trip, no rounding). No run_id: derived, not measured. The two algorithm tags drive re-derivation (store_meta keys
-- verdict.method / verdict.speechMethod record the build that derived the table).
CREATE TABLE verdict (
  location_id            INTEGER PRIMARY KEY REFERENCES track_location(id),
  hires_genuine          TEXT NOT NULL,
  hires_confidence       REAL,
  lossy_transcode_likely TEXT NOT NULL,
  lossy_confidence       REAL,
  speech_likely          TEXT NOT NULL,
  speech_confidence      REAL,
  method                 TEXT NOT NULL,
  speech_method          TEXT NOT NULL,
  prov                   TEXT NOT NULL DEFAULT '',
  content                TEXT NOT NULL DEFAULT '',
  updated_utc            TEXT NOT NULL
);
