-- truedat v2 store — schema v1. THE CONTRACT: reviewed cross-repo (restfulbee), proven by the byte-equal
-- round-trip against a real 72,477-track catalog, changed only by a new versioned script. Forward-only;
-- an exe refuses a store newer than itself. The design stands on its own (ruling 2026-10-11: the earlier
-- SST reference design is retired and is not the thing to reconcile against).
--
-- Two purposes live here and are kept apart (ruling 2026-10-11):
--   FILE TRACKING — per-machine truth about where bytes are: machine, library_root, file_entity,
--     identity_facet, track_location. Media is the primary key: file_entity.id is minted for the media,
--     independent of any location; the content hash is a facet of it, not the key. Locations stack
--     under it (machine → root → path); a common path tail is usual, not guaranteed, and is a MATCHING
--     rung (hash first, path second), never identity. This half is what would merge across devices.
--   SCANNING — measurements of the audio, the same whatever machine ran them: content-keyed (value,
--     segment, artifact). run and work_item are per-machine by nature.
--   The invariant: file 1 : locations many : audio description 1. track_feature (below) is the one
--   table that breaks it — a measurement of the AUDIO keyed by LOCATION, kept that way in v1 for the
--   byte-exact round-trip of a per-path catalog. It is the catalog's import/export mirror, not the
--   canonical measurement, and nothing (sync, merge, a new producer) may be built on it. Schema v2 splits
--   it into a content row once per entity and a location row per copy. Fleet merge/sync is out of
--   scope for now; these rules only keep the schema from closing that door.
--
-- Conventions: all timestamps ISO-8601 UTC text ('o' format), ids are UUIDv7 text where the id must
-- survive export (entity, machine) and INTEGER rowids otherwise; vectors are float64 little-endian
-- BLOBs (exactness first: mfcc is written at full double precision); smfmScores is int32 LE.
-- Every content hash carries its scheme (restfulbee ask, 2026-10-11): facet.kind names the algorithm,
-- and every other table that holds a hash has a *_kind column beside it, so "same content, new scheme"
-- and "same scheme, different content" are never the same key.

CREATE TABLE schema_version (
  version         INTEGER PRIMARY KEY,
  applied_utc     TEXT NOT NULL,
  truedat_version TEXT NOT NULL
);

CREATE TABLE store_meta (
  key   TEXT PRIMARY KEY,
  value TEXT
);

-- ---------------------------------------------------------------- identity (file tracking)
CREATE TABLE machine (
  id          TEXT PRIMARY KEY,            -- UUIDv7, minted by this store on creation
  hostname    TEXT NOT NULL,
  created_utc TEXT NOT NULL
);

CREATE TABLE library_root (
  id          INTEGER PRIMARY KEY,
  machine_id  TEXT NOT NULL REFERENCES machine(id),
  path        TEXT NOT NULL,               -- as given, original case
  role        TEXT NOT NULL CHECK (role IN ('primary','mirror','endpoint')),
  created_utc TEXT NOT NULL,
  UNIQUE (machine_id, path)
);

CREATE TABLE file_entity (
  id          TEXT PRIMARY KEY,            -- UUIDv7; the anchor that survives retag, move and re-encode
  media_type  TEXT NOT NULL DEFAULT 'music',
  state       TEXT NOT NULL DEFAULT 'active' CHECK (state IN ('active','deleted','merged','orphaned')),
  merged_into TEXT REFERENCES file_entity(id),
  created_utc TEXT NOT NULL,
  sequence    INTEGER NOT NULL DEFAULT 0   -- per-mutation counter; sync cursors later
);

-- A facet kind names its algorithm so a second hash can sit beside the first:
--   content_hash:audio-stream-sha256                 (primary today; FLAC frame-anchored since 2026-07-21)
--   content_hash:audio-stream-sha256:taglib-region   (pre-2026-07-21 FLAC value, kept forever, never primary)
--   content_hash:audio-stream-sha256:whole-file      (fallback when invariant bounds were unavailable)
--   path:canonical                                   (the only facet a no-sha entry has; confidence 0.5)
--   external_id:<provider>                           (truedat's own ids land here when the hub imports)
-- Facets are never deleted during merge. A superseded hash stays, so a key move is always
-- recoverable from the store (lesson of the 2026-10-11 FLAC-residuals finding).
CREATE TABLE identity_facet (
  id             INTEGER PRIMARY KEY,
  entity_id      TEXT NOT NULL REFERENCES file_entity(id),
  kind           TEXT NOT NULL,
  value          TEXT NOT NULL,
  is_primary     INTEGER NOT NULL DEFAULT 0,
  confidence     REAL NOT NULL DEFAULT 1.0,
  source         TEXT NOT NULL,            -- 'import:<catalog>' | 'scan' | 'verify' | ...
  first_seen_utc TEXT NOT NULL,
  last_seen_utc  TEXT NOT NULL,
  UNIQUE (entity_id, kind, value)
);
CREATE INDEX ix_facet_kind_value ON identity_facet(kind, value);

-- One row per copy of a file on a machine. Keyed (root, canonical path) so two copies of the same
-- audio on one machine both record (spec G2, what --duplicates finds).
CREATE TABLE track_location (
  id                INTEGER PRIMARY KEY,
  entity_id         TEXT NOT NULL REFERENCES file_entity(id),
  root_id           INTEGER NOT NULL REFERENCES library_root(id),
  rel_path          TEXT NOT NULL,         -- below root, original case, '/' separators
  canonical_path    TEXT NOT NULL,         -- root stripped, NFC, '/', case-folded (CanonicalPath.cs)
  display_path      TEXT NOT NULL,         -- the catalog key, byte for byte, so the export can key on it
  last_modified_utc TEXT,                  -- the catalog's lastModified (file mtime, 'o')
  last_seen_utc     TEXT NOT NULL,
  state             TEXT NOT NULL DEFAULT 'present' CHECK (state IN ('present','missing','retired')),
  UNIQUE (root_id, canonical_path)
);
CREATE INDEX ix_location_entity    ON track_location(entity_id);
CREATE INDEX ix_location_canonical ON track_location(canonical_path);

-- ---------------------------------------------------------------- provenance and work
CREATE TABLE producer (
  key         TEXT PRIMARY KEY,
  version     TEXT NOT NULL,
  kind        TEXT NOT NULL CHECK (kind IN ('analysis','import','verify')),
  needs_audio INTEGER NOT NULL DEFAULT 0
);
INSERT INTO producer (key, version, kind, needs_audio) VALUES
  ('essentia', 'unknown', 'analysis', 1),
  ('import',   '1',       'import',   0),
  ('verify',   '1',       'verify',   1);

-- One row per attempt. A run IS the observation record (spec §4): what was tried, on what, by which
-- tool, with what outcome. Imported ledgers become runs: errors.csv -> failed, skipped.csv -> skipped.
CREATE TABLE run (
  id                INTEGER PRIMARY KEY,
  producer_key      TEXT NOT NULL REFERENCES producer(key),
  producer_version  TEXT NOT NULL,
  entity_id         TEXT REFERENCES file_entity(id),
  content_hash      TEXT,
  content_hash_kind TEXT,                  -- facet kind of content_hash
  location_id       INTEGER REFERENCES track_location(id),
  started_utc       TEXT NOT NULL,
  duration_ms       INTEGER,
  host              TEXT,
  tool_version      TEXT,
  outcome           TEXT NOT NULL CHECK (outcome IN ('ok','failed','skipped')),
  failed_components TEXT,                  -- 'decode-27%;fingerprint;sha' as the errors CSV has it
  reason            TEXT,                  -- skip reason / rule
  error_text        TEXT,
  source            TEXT                   -- 'import:<catalog path>@<sha>:<bytes>' | ledger file path | ...
);
CREATE INDEX ix_run_entity  ON run(entity_id);
CREATE INDEX ix_run_outcome ON run(outcome);

CREATE TABLE work_item (
  id                INTEGER PRIMARY KEY,
  entity_id         TEXT NOT NULL REFERENCES file_entity(id),
  producer_key      TEXT NOT NULL REFERENCES producer(key),
  target_version    TEXT NOT NULL,
  state             TEXT NOT NULL DEFAULT 'queued' CHECK (state IN ('queued','leased','done','failed','parked')),
  priority          INTEGER NOT NULL DEFAULT 0,
  attempts          INTEGER NOT NULL DEFAULT 0,
  review_state      TEXT,                  -- from mbxmoods-review.json: 'auto' | 'operator' | ...
  review_note       TEXT,
  lease_until_utc   TEXT,
  next_eligible_utc TEXT,
  UNIQUE (entity_id, producer_key)
);
CREATE INDEX ix_work_state ON work_item(state);

-- Verification results (--verify CSV rows today; re-verify passes later). Named check_result
-- because CHECK is an SQL keyword.
CREATE TABLE check_result (
  id           INTEGER PRIMARY KEY,
  location_id  INTEGER REFERENCES track_location(id),
  content_hash TEXT,
  content_hash_kind TEXT,
  kind         TEXT NOT NULL,              -- 'verify'
  result       TEXT NOT NULL,              -- OK | DRIFT | MISSING | NO_HASH | ERROR | BACKFILLED | REANALYZE_NEEDED
  detail       TEXT,
  checked_utc  TEXT NOT NULL,
  source       TEXT
);
CREATE INDEX ix_check_location ON check_result(location_id);

CREATE TABLE conflict (
  id          INTEGER PRIMARY KEY,
  entity_a    TEXT NOT NULL REFERENCES file_entity(id),
  entity_b    TEXT REFERENCES file_entity(id),
  kind        TEXT NOT NULL,               -- 'sha-collision-props-differ' | ...
  detail      TEXT,
  state       TEXT NOT NULL DEFAULT 'open' CHECK (state IN ('open','resolved')),
  created_utc TEXT NOT NULL
);

-- ---------------------------------------------------------------- extensible edges (created empty in S1)
-- A new producer adds rows here, never tables (spec §3 rules 1-2). Keyed by content hash because
-- these are functions of the audio and the big ones (artifacts) must be stored once per audio.
CREATE TABLE value (
  content_hash     TEXT NOT NULL,
  content_hash_kind TEXT NOT NULL DEFAULT 'content_hash:audio-stream-sha256',
  field            TEXT NOT NULL,
  producer_key     TEXT NOT NULL REFERENCES producer(key),
  producer_version TEXT NOT NULL,
  run_id           INTEGER REFERENCES run(id),
  num              REAL,
  txt              TEXT,
  vec              BLOB,                   -- float32 LE
  dims             INTEGER,
  unit             TEXT,
  PRIMARY KEY (content_hash, field, producer_key, producer_version)
);

CREATE TABLE segment (
  id           INTEGER PRIMARY KEY,
  content_hash TEXT NOT NULL,
  content_hash_kind TEXT NOT NULL DEFAULT 'content_hash:audio-stream-sha256',
  set_key      TEXT NOT NULL,              -- producer + version that defined the segmentation
  kind         TEXT NOT NULL,              -- 'head' | 'tail' | 'frac8' | 'section' | 'speech' | ...
  ordinal      INTEGER NOT NULL,
  start_ms     INTEGER NOT NULL,           -- time-addressed, never frame-indexed (two frame grids)
  end_ms       INTEGER NOT NULL,
  UNIQUE (content_hash, set_key, kind, ordinal)
);

CREATE TABLE artifact (
  id             INTEGER PRIMARY KEY,
  content_hash   TEXT NOT NULL,
  content_hash_kind TEXT NOT NULL DEFAULT 'content_hash:audio-stream-sha256',
  run_id         INTEGER REFERENCES run(id),
  kind           TEXT NOT NULL,            -- 'main' | 'frames-head' | 'frames-tail' | ...
  format_version TEXT NOT NULL,            -- records grid/origin/profile so a re-fold lines up
  path           TEXT NOT NULL,            -- content-addressed file beside the database, not a blob
  bytes          INTEGER NOT NULL,
  sha256         TEXT NOT NULL,
  created_utc    TEXT NOT NULL,
  UNIQUE (content_hash, kind, format_version)
);

-- ---------------------------------------------------------------- the catalog entry, typed
-- One row per location (ruling 2026-10-11: the catalog is per path; two paths sharing a sha can differ
-- in per-copy facts, so collapsing by hash would make the round-trip lossy). content_key is indexed so
-- "same audio" is a query. THIS IS THE IMPORT/EXPORT MIRROR OF THE CATALOG, NOT THE CANONICAL
-- MEASUREMENT (header): schema v2 splits it into a content row (every Essentia descriptor, bitUsage,
-- HF, smfm — once per entity) and a location row (tags as found in the copy, fingerprint_v1_*, fileMd5,
-- lastModified, analysisDuration, imported_run — per copy); export becomes a join on content_key and
-- --store-roundtrip stays the gate. Column names are the catalog writer's JSON keys verbatim; nested blocks are flattened
-- with '_' and '.' becomes '_' (fingerprint.v1.fileSize -> fingerprint_v1_fileSize). The write-time
-- `truedat` verdict block is NOT stored: it is recomputed by the writer on export.
CREATE TABLE track_feature (
  location_id     INTEGER PRIMARY KEY REFERENCES track_location(id),
  content_key     TEXT NOT NULL,           -- audioStreamSha256, or 'entity:<uuid>' when the entry has none
  content_key_kind TEXT NOT NULL,          -- facet kind of content_key, or 'entity' for the no-sha fallback
  imported_run_id INTEGER REFERENCES run(id),
  updated_utc     TEXT NOT NULL,

  trackId INTEGER,
  artist TEXT, title TEXT, album TEXT, genre TEXT,
  bpm REAL, "key" TEXT, mode TEXT,
  spectralCentroid REAL, spectralFlux REAL, loudness REAL, danceability REAL,
  onsetRate REAL, zeroCrossingRate REAL, spectralRms REAL,
  spectralFlatness REAL, dissonance REAL, pitchSalience REAL, chordsChangesRate REAL,
  mfcc BLOB,
  dynamicRange REAL, dynamicRangeSource TEXT,
  loudnessMomentary REAL, loudnessShortTerm REAL, replayGain REAL,
  silenceRate20dB REAL, silenceRate30dB REAL, silenceRate60dB REAL,
  spectralRolloff REAL, spectralComplexity REAL, spectralEntropy REAL, spectralKurtosis REAL,
  spectralSkewness REAL, spectralSpread REAL, spectralStrongPeak REAL, spectralDecrease REAL,
  spectralEnergy REAL, spectralEnergyLow REAL, spectralEnergyMidLow REAL, spectralEnergyMidHigh REAL, spectralEnergyHigh REAL,
  hfc REAL,
  barkCrest REAL, barkFlatness REAL, barkKurtosis REAL, barkSkewness REAL, barkSpread REAL,
  erbCrest REAL, erbFlatness REAL, erbKurtosis REAL, erbSkewness REAL, erbSpread REAL,
  melCrest REAL, melFlatness REAL, melKurtosis REAL, melSkewness REAL, melSpread REAL,
  beatsLoudness REAL, chordsStrength REAL, hpcpCrest REAL, hpcpEntropy REAL,
  keyVotes_krumhansl_key TEXT, keyVotes_krumhansl_scale TEXT, keyVotes_krumhansl_strength REAL,
  keyVotes_temperley_key TEXT, keyVotes_temperley_scale TEXT, keyVotes_temperley_strength REAL,
  keyVotes_edma_key TEXT,      keyVotes_edma_scale TEXT,      keyVotes_edma_strength REAL,
  bpmFirstPeak REAL, bpmFirstPeakWeight REAL, bpmFirstPeakSpread REAL,
  bpmSecondPeak REAL, bpmSecondPeakWeight REAL, bpmSecondPeakSpread REAL,
  chordsKey TEXT, chordsScale TEXT, chordsHistogram BLOB, chordsNumberRate REAL,
  tuningFrequency REAL, tuningEqualTemperedDeviation REAL, tuningDiatonicStrength REAL, tuningNontemperedEnergyRatio REAL,
  averageLoudness REAL,
  hpcp12 BLOB, dynamicComplexity REAL, thpcp12 BLOB,
  beatsIntervalMean REAL, beatsIntervalStdev REAL, beatsIntervalMin REAL, beatsIntervalMax REAL,
  spectralCentroidStdev REAL, spectralEnergyStdev REAL, dissonanceStdev REAL, pitchSalienceStdev REAL,
  hpcpEntropyStdev REAL, zeroCrossingRateStdev REAL, mfccStdev BLOB,
  spectralContrastCoeffs BLOB, spectralContrastValleys BLOB, beatsLoudnessBandRatio BLOB, bpmHistogram BLOB, gfcc BLOB,
  bitUsage_lowestNonZeroBit INTEGER, bitUsage_bottomBitActivity REAL, bitUsage_effectiveBits REAL,
  bitUsage_samplesAnalyzed INTEGER, bitUsage_method TEXT,
  hfEnergyRatio REAL, hfEnergyMethod TEXT,
  hfSpectralStructure_flatness REAL, hfSpectralStructure_peakToMean REAL, hfSpectralStructure_imagingSymmetry REAL, hfSpectralStructure_method TEXT,
  smfmScores BLOB, smfmChannel INTEGER, smfmChannelName TEXT, smfmBpm REAL,
  analysisDuration REAL,
  fileMd5 TEXT,
  audioStreamSha256 TEXT, audioStreamSha256Source TEXT,
  fingerprint_v1_fileSize INTEGER, fingerprint_v1_pathTail TEXT, fingerprint_v1_durationMs INTEGER,
  fingerprint_v1_sampleRate INTEGER, fingerprint_v1_channels INTEGER, fingerprint_v1_bitDepth INTEGER,
  fingerprint_v1_codec TEXT, fingerprint_v1_codecRaw TEXT, fingerprint_v1_bitrate INTEGER,
  fingerprint_v1_encoder TEXT, fingerprint_v1_encoderRaw TEXT,
  fingerprint_v1_mp3LameTag_version TEXT, fingerprint_v1_mp3LameTag_infoTagRevision INTEGER,
  fingerprint_v1_mp3LameTag_vbrMethodCode INTEGER, fingerprint_v1_mp3LameTag_vbrMethod TEXT,
  fingerprint_v1_mp3LameTag_lowpassHz INTEGER, fingerprint_v1_mp3LameTag_encoderDelay INTEGER,
  fingerprint_v1_mp3LameTag_encoderPadding INTEGER, fingerprint_v1_mp3LameTag_musicCrc INTEGER,
  fingerprint_v1_audioHead64kMd5 TEXT, fingerprint_v1_audioHead64kMd5Source TEXT
);
CREATE INDEX ix_feature_content ON track_feature(content_key);
