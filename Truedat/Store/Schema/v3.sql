-- truedat store — schema v3 (2026-10-15, store v3 sprint T04). Forward-only; applied on top of v2 by SchemaMigrator inside
-- one transaction, after a VACUUM INTO backup of the v2 file; SchemaV3Migration.RunDataStep moves every row through the
-- mapper (v2 reader -> v3 binder), drops the v2 tables and renames these into place.
--
-- WHY: "sure why wouldn't we make the types correct" (operator). v2 held every fixed-decimal field as REAL (a float64 of a
-- value the parser had already rounded to 1..12 decimal places) and every vector as a float64 blob, and then rounded again
-- on the store path to six significant figures — a second, lossy precision rule (DBA review F1: spectralSpread differed
-- store-vs-catalog on 7,538 of 7,645 MB3 rows). v3 stores each numeric field at the precision FieldPrecision declares:
--   Fixed(dp)        INTEGER = round(value × 10^dp)          read back as stored / 10^dp = the double Math.Round(v, dp) made
--   FixedVec(dp)     BLOB    int32 × 10^dp, little-endian
--   SparseFixedVec   BLOB    u16 length, u16 count, (u16 index, int32 value) per non-zero   (bpmHistogram: ~94 % zeros)
--   Int / IntVec     INTEGER / BLOB int32                      unchanged
-- Column NAMES are unchanged: still the catalog writer's JSON keys, flattened; the sidecar's record layout and the catalog's
-- bytes are export contracts and never see these types. Nothing is lost: a stored integer reproduces the parsed decimal exactly.

-- ---------------------------------------------------------------- the audio description, once per media (typed)
CREATE TABLE track_content_v3 (
  entity_id        TEXT PRIMARY KEY REFERENCES file_entity(id),
  content_key      TEXT NOT NULL,
  content_key_kind TEXT NOT NULL,
  run_id           INTEGER REFERENCES run(id),
  updated_utc      TEXT NOT NULL,

  bpm INTEGER, "key" TEXT, mode TEXT,
  spectralCentroid INTEGER, spectralFlux INTEGER, loudness INTEGER, danceability INTEGER,
  onsetRate INTEGER, zeroCrossingRate INTEGER, spectralRms INTEGER,
  spectralFlatness INTEGER, dissonance INTEGER, pitchSalience INTEGER, chordsChangesRate INTEGER,
  mfcc BLOB,
  dynamicRange INTEGER, dynamicRangeSource TEXT,
  loudnessMomentary INTEGER, loudnessShortTerm INTEGER, replayGain INTEGER,
  silenceRate20dB INTEGER, silenceRate30dB INTEGER, silenceRate60dB INTEGER,
  spectralRolloff INTEGER, spectralComplexity INTEGER, spectralEntropy INTEGER, spectralKurtosis INTEGER,
  spectralSkewness INTEGER, spectralSpread INTEGER, spectralStrongPeak INTEGER, spectralDecrease INTEGER,
  spectralEnergy INTEGER, spectralEnergyLow INTEGER, spectralEnergyMidLow INTEGER, spectralEnergyMidHigh INTEGER, spectralEnergyHigh INTEGER,
  hfc INTEGER,
  barkCrest INTEGER, barkFlatness INTEGER, barkKurtosis INTEGER, barkSkewness INTEGER, barkSpread INTEGER,
  erbCrest INTEGER, erbFlatness INTEGER, erbKurtosis INTEGER, erbSkewness INTEGER, erbSpread INTEGER,
  melCrest INTEGER, melFlatness INTEGER, melKurtosis INTEGER, melSkewness INTEGER, melSpread INTEGER,
  beatsLoudness INTEGER, chordsStrength INTEGER, hpcpCrest INTEGER, hpcpEntropy INTEGER,
  keyVotes_krumhansl_key TEXT, keyVotes_krumhansl_scale TEXT, keyVotes_krumhansl_strength INTEGER,
  keyVotes_temperley_key TEXT, keyVotes_temperley_scale TEXT, keyVotes_temperley_strength INTEGER,
  keyVotes_edma_key TEXT,      keyVotes_edma_scale TEXT,      keyVotes_edma_strength INTEGER,
  bpmFirstPeak INTEGER, bpmFirstPeakWeight INTEGER, bpmFirstPeakSpread INTEGER,
  bpmSecondPeak INTEGER, bpmSecondPeakWeight INTEGER, bpmSecondPeakSpread INTEGER,
  chordsKey TEXT, chordsScale TEXT, chordsHistogram BLOB, chordsNumberRate INTEGER,
  tuningFrequency INTEGER, tuningEqualTemperedDeviation INTEGER, tuningDiatonicStrength INTEGER, tuningNontemperedEnergyRatio INTEGER,
  averageLoudness INTEGER,
  hpcp12 BLOB, dynamicComplexity INTEGER, thpcp12 BLOB,
  beatsIntervalMean INTEGER, beatsIntervalStdev INTEGER, beatsIntervalMin INTEGER, beatsIntervalMax INTEGER,
  spectralCentroidStdev INTEGER, spectralEnergyStdev INTEGER, dissonanceStdev INTEGER, pitchSalienceStdev INTEGER,
  hpcpEntropyStdev INTEGER, zeroCrossingRateStdev INTEGER, mfccStdev BLOB,
  spectralContrastCoeffs BLOB, spectralContrastValleys BLOB, beatsLoudnessBandRatio BLOB, bpmHistogram BLOB, gfcc BLOB,
  bitUsage_lowestNonZeroBit INTEGER, bitUsage_bottomBitActivity INTEGER, bitUsage_effectiveBits INTEGER,
  bitUsage_samplesAnalyzed INTEGER, bitUsage_method TEXT,
  hfEnergyRatio INTEGER, hfEnergyMethod TEXT,
  hfSpectralStructure_flatness INTEGER, hfSpectralStructure_peakToMean INTEGER, hfSpectralStructure_imagingSymmetry INTEGER, hfSpectralStructure_method TEXT,
  audioStreamSha256 TEXT, audioStreamSha256Source TEXT
);

-- ---------------------------------------------------------------- the copy's own facts, once per location (typed)
CREATE TABLE track_copy_v3 (
  location_id INTEGER PRIMARY KEY REFERENCES track_location(id),
  run_id      INTEGER REFERENCES run(id),
  updated_utc TEXT NOT NULL,

  trackId INTEGER,
  artist TEXT, title TEXT, album TEXT, genre TEXT,
  smfmScores BLOB, smfmChannel INTEGER, smfmChannelName TEXT, smfmBpm INTEGER,
  analysisDuration INTEGER,
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
