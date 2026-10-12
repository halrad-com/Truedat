-- truedat v2 store — schema v2 (2026-10-12). Forward-only; applied on top of v1 by SchemaMigrator, with
-- SchemaV2Migration.RunDataStep moving the rows and dropping track_feature inside the same transaction.
--
-- WHY: the operator's invariant — file 1 : locations many : audio description 1 ("files sync. metadata
-- follows. and should be able to merge and join and be the same across the same file in different
-- locations"). v1's track_feature was one wide row per LOCATION, so a file in three places had three
-- descriptions that merely happened to agree. v2 splits it:
--   track_content  one row per MEDIA (file_entity): every Essentia descriptor, bitUsage, the HF signals,
--                  and the content hash itself. Functions of the audio; the same whatever copy or machine
--                  measured them. This is the SCANNING half of the header's boundary.
--   track_copy     one row per LOCATION: tags as found in that copy, fingerprint_v1_* (file facts),
--                  fileMd5, SMFM (read from that copy's tags), analysisDuration. The FILE TRACKING half.
-- Export = track_copy ⋈ track_location ⋈ track_content on the location's entity; --store-roundtrip stays
-- the gate. Column names are still the catalog writer's JSON keys, flattened as in v1.

-- ---------------------------------------------------------------- the audio description, once per media
CREATE TABLE track_content (
  entity_id        TEXT PRIMARY KEY REFERENCES file_entity(id),
  content_key      TEXT NOT NULL,          -- audioStreamSha256, or 'entity:<uuid>' when the media has none
  content_key_kind TEXT NOT NULL,          -- facet kind of content_key, or 'entity' for the no-sha fallback
  run_id           INTEGER REFERENCES run(id),
  updated_utc      TEXT NOT NULL,

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
  audioStreamSha256 TEXT, audioStreamSha256Source TEXT
);
CREATE INDEX ix_content_key ON track_content(content_key);

-- ---------------------------------------------------------------- the copy's own facts, once per location
CREATE TABLE track_copy (
  location_id INTEGER PRIMARY KEY REFERENCES track_location(id),
  run_id      INTEGER REFERENCES run(id),
  updated_utc TEXT NOT NULL,

  trackId INTEGER,
  artist TEXT, title TEXT, album TEXT, genre TEXT,
  smfmScores BLOB, smfmChannel INTEGER, smfmChannelName TEXT, smfmBpm REAL,   -- read from THIS copy's tags; copies differ
  analysisDuration REAL,
  fileMd5 TEXT,
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

-- ---------------------------------------------------------------- value hangs off a segment (S2 C4)
-- segment_id 0 = the whole track; otherwise a segment row. No FK on it because 0 names no row and the
-- primary key must not hold NULL (SQLite treats NULLs in a non-INTEGER-PK key as distinct).
CREATE TABLE value_v2 (
  content_hash      TEXT NOT NULL,
  content_hash_kind TEXT NOT NULL DEFAULT 'content_hash:audio-stream-sha256',
  segment_id        INTEGER NOT NULL DEFAULT 0,
  field             TEXT NOT NULL,
  producer_key      TEXT NOT NULL REFERENCES producer(key),
  producer_version  TEXT NOT NULL,
  run_id            INTEGER REFERENCES run(id),
  num               REAL,
  txt               TEXT,
  vec               BLOB,                  -- little-endian; dtype is the producer's contract (float32 or int16)
  dims              INTEGER,
  unit              TEXT,
  PRIMARY KEY (content_hash, segment_id, field, producer_key, producer_version)
);
INSERT INTO value_v2 (content_hash, content_hash_kind, segment_id, field, producer_key, producer_version, run_id, num, txt, vec, dims, unit)
  SELECT content_hash, content_hash_kind, 0, field, producer_key, producer_version, run_id, num, txt, vec, dims, unit FROM value;
DROP TABLE value;
ALTER TABLE value_v2 RENAME TO value;

-- ---------------------------------------------------------------- conflicts are queryable by field and run (S2 C2)
ALTER TABLE conflict ADD COLUMN field   TEXT;
ALTER TABLE conflict ADD COLUMN run_a   INTEGER REFERENCES run(id);
ALTER TABLE conflict ADD COLUMN run_b   INTEGER REFERENCES run(id);
ALTER TABLE conflict ADD COLUMN value_a TEXT;
ALTER TABLE conflict ADD COLUMN value_b TEXT;
-- Data move (track_feature -> track_content + track_copy, disagreements -> conflict, DROP track_feature)
-- is SchemaV2Migration.RunDataStep, which runs right after this script in the same transaction.
