using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Truedat
{
    /// <summary>
    /// Writes the <c>.mbxs</c> binary catalog sidecar beside <c>mbxmoods.json</c> on every save.
    /// It is a pre-compiled projection of the AutoQ scoring hot-path fields: MBXHub's MoodCache
    /// loads it ~orders of magnitude faster than parsing the JSON, version-checks it, and falls
    /// back to the JSON when it is absent or a version it doesn't recognise. The JSON stays the
    /// canonical, human-readable source of truth; the sidecar is a rebuildable derived cache.
    /// truedat owns the WRITER; MBXHub owns the reader.
    ///
    /// ============================ .mbxs FORMAT v3 (little-endian) ============================
    /// v3 appends the 9 remaining raw model-input scalars (the "grow list") + a per-row JSON
    /// offset/length seek table to the v2 record; every v2 offset is unchanged (append-only). With
    /// v3 the sidecar carries ALL 21 raw scalars the AutoQ mood model reads, so MBXHub loads the
    /// mood cache from the ~24 MB sidecar (~0.2 s) instead of parsing the ~540 MB JSON (~23.6 s),
    /// deriving V/A byte-identically. v2 widened the Rec from v1's 5 fields to MBXHub's
    /// ContinuityScorer PfsPoint set, EXCEPT what truedat does not author. Valence and Arousal are
    /// NOT stored — they are the output of MBXHub's mood-model (a hub-side setting the operator
    /// retrains WITHOUT a rescan), so baking them in would make the file wrong the instant the model
    /// changes. Same reason CamelotCode, BpmNorm and modeMajor are absent (derivable reader-side:
    /// camelot/modeMajor from keyCode, bpmNorm from a hub bpm setting, smfmValence/Arousal from the
    /// SMFM block). Instead the sidecar carries the RAW feature inputs the
    /// mood-model consumes — the general + arousal + valence scalars AND the Sony 12-TONE SMFM
    /// block (the 24-feature model is an SMFM-fusion model) — so the hub derives V/A at load
    /// exactly as it does from the JSON. AcousticRanks (BpmPct/CentroidPct/DancePct/OnsetPct/
    /// RmsPct/FluxPct) are likewise carried as their RAW values — the percentile ranks are
    /// catalog-relative and shift as the library grows, so the hub ranks them at load (same as
    /// from JSON), keeping the file valid.
    ///
    /// Header (20 bytes):
    ///   char[4]  magic       = "MBXS"
    ///   u32      version     = 3
    ///   u32      trackCount  = N
    ///   u32      chromaFloats= 24            (hpcp12[12] ++ thpcp12[12])
    ///   u32      smfmSlots   = 12            (Sony 12-TONE STMO slot scores)
    ///
    /// Genre table (immediately after the header):
    ///   u32      genreCount  = G
    ///   (u16 byteLen + UTF-8 bytes)[G]       deduped genre strings; Rec.GenreId indexes this,
    ///                                        0xFFFF = no genre.
    ///
    /// Rec[N]  — fixed 166-byte stride (v2 was 85, first v3 cut was 133), SORTED ASCENDING by PathHash
    ///           (O(log N) binary-search random access with no parsing):
    ///   i64  PathHash          FNV-1a 64-bit over the UTF-16 chars of the catalog key (the file
    ///                          path, exactly the JSON "tracks" key). See FnvPathHash.
    ///   f32  RawBpm            rhythm.bpm                       (NaN if absent)
    ///   f32  RawBpmAlt         bpm_histogram_second_peak_bpm    (NaN if absent) — half/double evidence
    ///   f32  RawBpmAltWeight   bpm_histogram_second_peak_weight (NaN if absent)
    ///   f32  DynCx             dynamicComplexity                (NaN if absent)
    ///   i32  KeyCode           pitchClass(0..11)*2 + (minor?1:0) -> 0..23; -1 absent/unrecognised.
    ///                          (Replaces the labs prototype's string.GetHashCode(), which is NOT
    ///                          portable across runtimes — this code is both stable and decodable.)
    ///   i16  KeyAgreement      # of the 3 key profiles (krumhansl/temperley/edma) whose (key,scale)
    ///                          matches the FLAT key/mode camelot derives from; -1 when no votes.
    ///   u16  KeyVotesPresent   # of the 3 profiles present (0..3).
    ///   f32  Centroid          spectralCentroid   } the raw acoustic-6 the hub percentile-ranks
    ///   f32  Flux              spectralFlux        } into AcousticRanks (BpmPct uses RawBpm above).
    ///   f32  Loudness          loudness            }
    ///   f32  Dance             danceability        } general + arousal + valence raw scalars =
    ///   f32  Onset             onsetRate           } the mood-model's documented inputs, so the
    ///   f32  Zcr               zeroCrossingRate    } hub derives V/A at load. NaN never used here
    ///   f32  Rms               spectralRms         } (these are always-present core features).
    ///   f32  Flatness          spectralFlatness    }
    ///   f32  Dissonance        dissonance          }
    ///   f32  PitchSalience     pitchSalience       }
    ///   f32  ChordsChanges     chordsChangesRate   }
    ///   f32  SmfmBpm           Sony GBPM           (NaN if no SMFM tag)
    ///   u16  GenreId           index into the genre table; 0xFFFF = no genre.
    ///   u8   HasHpcp           1 if this row's hpcp12 chroma is real, 0 if the 12 floats are NaN.
    ///   u8   HasThpcp          1 if this row's thpcp12 chroma is real, 0 otherwise.
    ///   u8   HasSmfm           1 if this row's SMFM slot scores are real, 0 if NaN filler.
    ///   ---- v3 append (RecBytesV3 = 133) ----
    ///   f32  SpectralSkewness  } the 9 remaining raw model-input scalars ("grow list") the AutoQ
    ///   f32  SpectralEntropy   } mood model reads. NaN = absent. All already emitted to mbxmoods.json
    ///   f32  SpectralComplexity} today (nullable extended fields) -> writer projection, NO rescan.
    ///   f32  HpcpCrest         } Carried so MBXHub derives V/A from the sidecar byte-identical to the
    ///   f32  HpcpEntropy       } JSON path with no mean-imputation. spectralFlatness (above) is the
    ///   f32  Hfc               } known phantom (~0); carried as-is to keep the vector complete.
    ///   f32  BeatsLoudness     }
    ///   f32  ChordsStrength    }
    ///   f32  DynamicRange      }  (distinct from DynCx = dynamicComplexity above)
    ///   i64  JsonOffset        byte offset of this row's JSON object in the co-written mbxmoods.json.
    ///   i32  JsonLength        byte length of that object — the lazy cold-read seek table; 0 until
    ///                          populated by the writer (offsets valid only against the plain live JSON).
    ///   ---- v3 amend (166-byte stride, version STAYS 3 — amended in place, pre-first-consumption) ----
    ///   u8[32] Sha             audioStreamSha256 as 32 RAW bytes (the SHA-256 decoded from the 64-hex
    ///                          JSON string). ALL-ZERO (32 zero bytes) when absent (null / not 64 hex).
    ///                          MBXHub builds its boot hash index (cross-box / moved-file resolution +
    ///                          tuned V/A) on this — a sidecar-only boot without it comes up empty.
    ///   u8   SpeechLikely      AutoQ's per-pick speech-exclude gate. 0 = absent/none, 1 = yes, 2 = no,
    ///                          3 = unknown, 4 = n/a. Computed at write time from the SAME verdict path
    ///                          the JSON uses (Program.ComputeTruedatVerdict), so it is carry-not-compute.
    ///
    /// Chroma  — f32[N * 24], row i (same sorted order as Rec[i]) = hpcp12[0..11] ++ thpcp12[0..11].
    ///           Missing vectors are written as 12 NaN floats and flagged via HasHpcp/HasThpcp.
    ///
    /// Smfm    — f32[N * 12], row i = the Sony 12-TONE STMO slot scores (0-255). Shorter arrays are
    ///           right-padded and longer ones truncated to 12; missing = 12 NaN, flagged HasSmfm.
    ///
    /// Path table — (u16 byteLen + UTF-8 bytes)[N], same sorted order, for display/reconstruction.
    ///           byteLen is clamped to 65535 (paths are never that long in practice).
    /// ========================================================================================
    ///
    /// Any layout change is a cross-repo contract change with MBXHub: bump <c>version</c> (the
    /// reader declines an unknown version and falls back to JSON, so a bump is safe and an
    /// unversioned change is not) and hand MBXHub the new byte layout before shipping.
    /// </summary>
    internal static class CatalogSidecar
    {
        internal const int ChromaFloats = 24;   // hpcp12[12] + thpcp12[12]
        internal const int SmfmSlots = 12;      // Sony 12-TONE raw STMO slot scores
        internal const uint FormatVersion = 3;
        private const ushort NoGenre = 0xFFFF;

        /// <summary>FNV-1a 64-bit over the string's UTF-16 chars. Matches the reader; used both as
        /// the Rec sort key and the random-access lookup key. Char-based (not byte-based) on purpose
        /// so writer and reader agree without a shared encoding assumption.</summary>
        internal static long FnvPathHash(string s)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
                return (long)h;
            }
        }

        // Sharp and flat spellings -> pitch class 0..11 (C=0). Essentia's key profiles emit sharps;
        // flats are accepted too so a re-tag or a different profile can't silently fall to -1.
        private static readonly Dictionary<string, int> PitchClasses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            {"C",0},{"C#",1},{"Db",1},{"D",2},{"D#",3},{"Eb",3},{"E",4},{"F",5},
            {"F#",6},{"Gb",6},{"G",7},{"G#",8},{"Ab",8},{"A",9},{"A#",10},{"Bb",10},{"B",11},
        };

        /// <summary>Stable, decodable key code: pitchClass(0..11)*2 + (minor?1:0) -> 0..23; -1 when
        /// the key is absent/unrecognized. Never uses GetHashCode (non-portable across runtimes).</summary>
        internal static int KeyCode(string key, string mode)
        {
            if (string.IsNullOrEmpty(key) || !PitchClasses.TryGetValue(key.Trim(), out var pc)) return -1;
            bool minor = string.Equals(mode?.Trim(), "minor", StringComparison.OrdinalIgnoreCase);
            return pc * 2 + (minor ? 1 : 0);
        }

        /// <summary>Number of the three key profiles present (non-null), 0..3.</summary>
        internal static int KeyVotesPresent(TrackFeatures f)
        {
            if (f == null) return 0;
            int n = 0;
            if (f.KeyVoteKrumhansl != null) n++;
            if (f.KeyVoteTemperley != null) n++;
            if (f.KeyVoteEdma != null) n++;
            return n;
        }

        /// <summary>How many present profiles agree with the FLAT key/mode (edma, the one camelot
        /// derives from) — the trust weight for the harmonic term. -1 when no profile is present.</summary>
        internal static int KeyAgreement(TrackFeatures f)
        {
            if (f == null || KeyVotesPresent(f) == 0) return -1;
            int agree = 0;
            foreach (var v in new[] { f.KeyVoteKrumhansl, f.KeyVoteTemperley, f.KeyVoteEdma })
            {
                if (v == null) continue;
                if (string.Equals(v.Key?.Trim(), f.Key?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(v.Scale?.Trim(), f.Mode?.Trim(), StringComparison.OrdinalIgnoreCase))
                    agree++;
            }
            return agree;
        }

        private static float F(double v) => (float)v;
        private static float FN(double? v) => v.HasValue ? (float)v.Value : float.NaN;

        // 32 all-zero bytes = the "sha absent" sentinel written for a null / non-64-hex audioStreamSha256.
        private static readonly byte[] ZeroSha = new byte[32];

        /// <summary>Decode a 64-hex audioStreamSha256 to 32 raw bytes. Returns null (→ 32 zero bytes on
        /// write) when the string is null, not exactly 64 chars, or contains a non-hex character.</summary>
        private static byte[] DecodeShaHex(string hex)
        {
            if (hex == null || hex.Length != 64) return null;
            var b = new byte[32];
            for (int i = 0; i < 32; i++)
            {
                int hi = HexVal(hex[i * 2]);
                int lo = HexVal(hex[i * 2 + 1]);
                if (hi < 0 || lo < 0) return null;
                b[i] = (byte)((hi << 4) | lo);
            }
            return b;
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>Map ComputeTruedatVerdict's SpeechLikely string to the u8 enum. 0 = absent/none,
        /// 1 = yes, 2 = no, 3 = unknown, 4 = n/a (an unrecognised string also folds to 0).</summary>
        private static byte SpeechEnum(string s)
        {
            switch (s)
            {
                case "yes": return 1;
                case "no": return 2;
                case "unknown": return 3;
                case "n/a": return 4;
                default: return 0;   // null / empty / unrecognised
            }
        }

        /// <summary>Build and atomically write the sidecar for the whole catalog. Best-effort:
        /// throws only to its caller's try/catch — a sidecar failure must never fail a JSON save,
        /// because the sidecar is a rebuildable cache and the reader falls back to JSON.</summary>
        internal static void Write(string sidecarPath, ICollection<KeyValuePair<string, TrackEntry>> tracks)
        {
            int n = tracks.Count;
            var rec = new Rec[n];
            var chroma = new float[n * ChromaFloats];
            var smfm = new float[n * SmfmSlots];
            var paths = new string[n];

            // Deduped genre table, built in first-seen order; empty genre -> NoGenre.
            var genreIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var genres = new List<string>();

            int idx = 0;
            foreach (var kv in tracks)
            {
                var f = kv.Value?.Features;
                paths[idx] = kv.Key;
                var r = new Rec { PathHash = FnvPathHash(kv.Key) };
                int chOff = idx * ChromaFloats;
                int smOff = idx * SmfmSlots;
                // v3-amend hot fields. Sha lives on TrackEntry (not Features), so decode it regardless of
                // whether features exist; null (absent / non-64-hex) is written as 32 zero bytes.
                r.Sha = DecodeShaHex(kv.Value?.AudioStreamSha256);
                if (f != null)
                {
                    r.RawBpm = F(f.Bpm);
                    r.RawBpmAlt = FN(f.BpmSecondPeak);
                    r.RawBpmAltWeight = FN(f.BpmSecondPeakWeight);
                    r.DynCx = FN(f.DynamicComplexity);
                    r.KeyCode = KeyCode(f.Key, f.Mode);
                    r.KeyAgreement = (short)KeyAgreement(f);
                    r.KeyVotesPresent = (ushort)KeyVotesPresent(f);
                    r.Centroid = F(f.SpectralCentroid);
                    r.Flux = F(f.SpectralFlux);
                    r.Loudness = F(f.Loudness);
                    r.Dance = F(f.Danceability);
                    r.Onset = F(f.OnsetRate);
                    r.Zcr = F(f.ZeroCrossingRate);
                    r.Rms = F(f.SpectralRms);
                    r.Flatness = F(f.SpectralFlatness);
                    r.Dissonance = F(f.Dissonance);
                    r.PitchSalience = F(f.PitchSalience);
                    r.ChordsChanges = F(f.ChordsChangesRate);
                    r.SmfmBpm = FN(f.SmfmBpm);
                    r.GenreId = GenreId(f.Genre, genreIndex, genres);
                    r.SpectralSkewness = FN(f.SpectralSkewness);
                    r.SpectralEntropy = FN(f.SpectralEntropy);
                    r.SpectralComplexity = FN(f.SpectralComplexity);
                    r.HpcpCrest = FN(f.HpcpCrest);
                    r.HpcpEntropy = FN(f.HpcpEntropy);
                    r.Hfc = FN(f.Hfc);
                    r.BeatsLoudness = FN(f.BeatsLoudness);
                    r.ChordsStrength = FN(f.ChordsStrength);
                    r.DynamicRange = FN(f.DynamicRange);
                    FillChroma(chroma, chOff, f.Hpcp12, out bool h);
                    FillChroma(chroma, chOff + 12, f.Thpcp12, out bool th);
                    FillSmfm(smfm, smOff, f.SmfmScores, out bool hs);
                    r.HasHpcp = (byte)(h ? 1 : 0);
                    r.HasThpcp = (byte)(th ? 1 : 0);
                    r.HasSmfm = (byte)(hs ? 1 : 0);
                    // Same write-time verdict path the JSON uses, so carry-not-compute (no rescan).
                    r.SpeechLikely = SpeechEnum(Program.ComputeTruedatVerdict(kv.Key, kv.Value).SpeechLikely);
                }
                else
                {
                    r.RawBpm = r.RawBpmAlt = r.RawBpmAltWeight = r.DynCx = float.NaN;
                    r.Centroid = r.Flux = r.Loudness = r.Dance = r.Onset = r.Zcr = r.Rms =
                        r.Flatness = r.Dissonance = r.PitchSalience = r.ChordsChanges = r.SmfmBpm = float.NaN;
                    r.SpectralSkewness = r.SpectralEntropy = r.SpectralComplexity = r.HpcpCrest =
                        r.HpcpEntropy = r.Hfc = r.BeatsLoudness = r.ChordsStrength = r.DynamicRange = float.NaN;
                    r.KeyCode = -1; r.KeyAgreement = -1; r.KeyVotesPresent = 0; r.GenreId = NoGenre;
                    FillChroma(chroma, chOff, null, out _);
                    FillChroma(chroma, chOff + 12, null, out _);
                    FillSmfm(smfm, smOff, null, out _);
                }
                rec[idx] = r;
                idx++;
            }

            // Sort rows + chroma + smfm in lockstep by PathHash ascending (binary-search random access).
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => rec[a].PathHash.CompareTo(rec[b].PathHash));

            var tmp = sidecarPath + ".tmp";
            try { File.Delete(tmp); } catch { }
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes("MBXS"));
                bw.Write(FormatVersion);
                bw.Write((uint)n);
                bw.Write((uint)ChromaFloats);
                bw.Write((uint)SmfmSlots);

                bw.Write((uint)genres.Count);
                foreach (var g in genres) WriteString(bw, g);

                foreach (var o in order)
                {
                    var r = rec[o];
                    bw.Write(r.PathHash);
                    bw.Write(r.RawBpm); bw.Write(r.RawBpmAlt); bw.Write(r.RawBpmAltWeight); bw.Write(r.DynCx);
                    bw.Write(r.KeyCode); bw.Write(r.KeyAgreement); bw.Write(r.KeyVotesPresent);
                    bw.Write(r.Centroid); bw.Write(r.Flux); bw.Write(r.Loudness); bw.Write(r.Dance);
                    bw.Write(r.Onset); bw.Write(r.Zcr); bw.Write(r.Rms); bw.Write(r.Flatness);
                    bw.Write(r.Dissonance); bw.Write(r.PitchSalience); bw.Write(r.ChordsChanges); bw.Write(r.SmfmBpm);
                    bw.Write(r.GenreId); bw.Write(r.HasHpcp); bw.Write(r.HasThpcp); bw.Write(r.HasSmfm);
                    // v3 append: 9 model-input scalars (r+85..r+117) + JSON offset/length (r+121, r+129) -> 133 B stride.
                    bw.Write(r.SpectralSkewness); bw.Write(r.SpectralEntropy); bw.Write(r.SpectralComplexity);
                    bw.Write(r.HpcpCrest); bw.Write(r.HpcpEntropy); bw.Write(r.Hfc);
                    bw.Write(r.BeatsLoudness); bw.Write(r.ChordsStrength); bw.Write(r.DynamicRange);
                    bw.Write(r.JsonOffset); bw.Write(r.JsonLength);
                    // v3 amend (r+133..r+165 -> 166 B stride): audioStreamSha256 (32 raw bytes, all-zero
                    // when absent) + speechLikely (u8 enum). Carried for a sidecar-only hub boot.
                    bw.Write(r.Sha ?? ZeroSha); bw.Write(r.SpeechLikely);
                }
                foreach (var o in order)
                {
                    int b = o * ChromaFloats;
                    for (int j = 0; j < ChromaFloats; j++) bw.Write(chroma[b + j]);
                }
                foreach (var o in order)
                {
                    int b = o * SmfmSlots;
                    for (int j = 0; j < SmfmSlots; j++) bw.Write(smfm[b + j]);
                }
                foreach (var o in order) WriteString(bw, paths[o]);
                bw.Flush();
                fs.Flush(true);   // fsync: the sidecar is rebuildable, but don't leave a torn file
            }
            Program.AtomicReplace(tmp, sidecarPath, null);
        }

        private static ushort GenreId(string genre, Dictionary<string, int> index, List<string> genres)
        {
            if (string.IsNullOrEmpty(genre)) return NoGenre;
            if (!index.TryGetValue(genre, out var id))
            {
                id = genres.Count;
                if (id >= NoGenre) return NoGenre;   // >65534 distinct genres is impossible; be safe
                index[genre] = id;
                genres.Add(genre);
            }
            return (ushort)id;
        }

        private static void WriteString(BinaryWriter bw, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            int len = Math.Min(b.Length, 65535);
            bw.Write((ushort)len);
            bw.Write(b, 0, len);
        }

        private static string ReadString(BinaryReader br)
        {
            int len = br.ReadUInt16();
            return Encoding.UTF8.GetString(br.ReadBytes(len));
        }

        private static void FillChroma(float[] dst, int off, double[] vec, out bool present)
        {
            if (vec != null && vec.Length == 12)
            {
                for (int i = 0; i < 12; i++) dst[off + i] = (float)vec[i];
                present = true;
            }
            else
            {
                for (int i = 0; i < 12; i++) dst[off + i] = float.NaN;
                present = false;
            }
        }

        private static void FillSmfm(float[] dst, int off, int[] scores, out bool present)
        {
            if (scores != null && scores.Length > 0)
            {
                for (int i = 0; i < SmfmSlots; i++)
                    dst[off + i] = i < scores.Length ? scores[i] : float.NaN;   // pad short, truncate long
                present = true;
            }
            else
            {
                for (int i = 0; i < SmfmSlots; i++) dst[off + i] = float.NaN;
                present = false;
            }
        }

        private struct Rec
        {
            public long PathHash;
            public float RawBpm, RawBpmAlt, RawBpmAltWeight, DynCx;
            public int KeyCode;
            public short KeyAgreement;
            public ushort KeyVotesPresent;
            public float Centroid, Flux, Loudness, Dance, Onset, Zcr, Rms, Flatness, Dissonance, PitchSalience, ChordsChanges, SmfmBpm;
            public ushort GenreId;
            public byte HasHpcp, HasThpcp, HasSmfm;
            // v3 model-input scalars (the grow list) — NaN = absent, mirroring FN().
            public float SpectralSkewness, SpectralEntropy, SpectralComplexity, HpcpCrest, HpcpEntropy, Hfc, BeatsLoudness, ChordsStrength, DynamicRange;
            // v3 per-row JSON object span into the co-written mbxmoods.json (the lazy-read seek table); 0 until populated.
            public long JsonOffset;
            public int JsonLength;
            // v3 amend (hot non-model fields carried for a sidecar-only boot):
            // Sha = audioStreamSha256 as 32 raw bytes, null meaning all-zero (absent). SpeechLikely = u8 enum
            // (0 none, 1 yes, 2 no, 3 unknown, 4 n/a) from Program.ComputeTruedatVerdict.
            public byte[] Sha;
            public byte SpeechLikely;
        }

        /// <summary>Parsed sidecar, in stored (PathHash-sorted) order. Reference reader for the
        /// self-test and for any truedat-side diagnostics; MBXHub ships its own MoodCache reader.</summary>
        internal sealed class SidecarData
        {
            public int Version, TrackCount;
            public long[] PathHash = Array.Empty<long>();
            public float[] RawBpm = Array.Empty<float>();
            public float[] RawBpmAlt = Array.Empty<float>();
            public float[] RawBpmAltWeight = Array.Empty<float>();
            public float[] DynCx = Array.Empty<float>();
            public int[] KeyCode = Array.Empty<int>();
            public short[] KeyAgreement = Array.Empty<short>();
            public ushort[] KeyVotesPresent = Array.Empty<ushort>();
            public float[] Centroid = Array.Empty<float>();
            public float[] Flux = Array.Empty<float>();
            public float[] Loudness = Array.Empty<float>();
            public float[] Dance = Array.Empty<float>();
            public float[] Onset = Array.Empty<float>();
            public float[] Zcr = Array.Empty<float>();
            public float[] Rms = Array.Empty<float>();
            public float[] Flatness = Array.Empty<float>();
            public float[] Dissonance = Array.Empty<float>();
            public float[] PitchSalience = Array.Empty<float>();
            public float[] ChordsChanges = Array.Empty<float>();
            public float[] SmfmBpm = Array.Empty<float>();
            public float[] SpectralSkewness = Array.Empty<float>();
            public float[] SpectralEntropy = Array.Empty<float>();
            public float[] SpectralComplexity = Array.Empty<float>();
            public float[] HpcpCrest = Array.Empty<float>();
            public float[] HpcpEntropy = Array.Empty<float>();
            public float[] Hfc = Array.Empty<float>();
            public float[] BeatsLoudness = Array.Empty<float>();
            public float[] ChordsStrength = Array.Empty<float>();
            public float[] DynamicRange = Array.Empty<float>();
            public long[] JsonOffset = Array.Empty<long>();
            public int[] JsonLength = Array.Empty<int>();
            public byte[][] Sha = Array.Empty<byte[]>();       // per-row 32 raw bytes; all-zero when absent
            public byte[] SpeechLikely = Array.Empty<byte>();  // per-row u8 enum (0 none/1 yes/2 no/3 unknown/4 n-a)
            public string[] Genre = Array.Empty<string>();   // resolved per-row (null when absent)
            public byte[] HasHpcp = Array.Empty<byte>();
            public byte[] HasThpcp = Array.Empty<byte>();
            public byte[] HasSmfm = Array.Empty<byte>();
            public float[] Chroma = Array.Empty<float>();   // TrackCount * 24
            public float[] Smfm = Array.Empty<float>();     // TrackCount * 12
            public string[] Paths = Array.Empty<string>();

            /// <summary>O(log N) lookup by path -> row index, or -1. Mirrors the reader's hot path.</summary>
            public int IndexOf(string path)
            {
                long target = FnvPathHash(path);
                int lo = 0, hi = TrackCount - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    long v = PathHash[mid];
                    if (v == target) return mid;
                    if (v < target) lo = mid + 1; else hi = mid - 1;
                }
                return -1;
            }
        }

        internal static SidecarData Read(string sidecarPath)
        {
            using (var fs = new FileStream(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                var magic = Encoding.ASCII.GetString(br.ReadBytes(4));
                if (magic != "MBXS") throw new InvalidDataException($"not a .mbxs file (magic '{magic}')");
                var d = new SidecarData { Version = (int)br.ReadUInt32() };
                if (d.Version != FormatVersion) throw new InvalidDataException($"unsupported .mbxs version {d.Version} (this build reads v{FormatVersion})");
                int n = (int)br.ReadUInt32();
                int chromaFloats = (int)br.ReadUInt32();
                if (chromaFloats != ChromaFloats) throw new InvalidDataException($"unexpected chromaFloats {chromaFloats}");
                int smfmSlots = (int)br.ReadUInt32();
                if (smfmSlots != SmfmSlots) throw new InvalidDataException($"unexpected smfmSlots {smfmSlots}");
                d.TrackCount = n;

                int g = (int)br.ReadUInt32();
                var genreTable = new string[g];
                for (int i = 0; i < g; i++) genreTable[i] = ReadString(br);

                d.PathHash = new long[n];
                d.RawBpm = new float[n]; d.RawBpmAlt = new float[n]; d.RawBpmAltWeight = new float[n]; d.DynCx = new float[n];
                d.KeyCode = new int[n]; d.KeyAgreement = new short[n]; d.KeyVotesPresent = new ushort[n];
                d.Centroid = new float[n]; d.Flux = new float[n]; d.Loudness = new float[n]; d.Dance = new float[n];
                d.Onset = new float[n]; d.Zcr = new float[n]; d.Rms = new float[n]; d.Flatness = new float[n];
                d.Dissonance = new float[n]; d.PitchSalience = new float[n]; d.ChordsChanges = new float[n]; d.SmfmBpm = new float[n];
                d.SpectralSkewness = new float[n]; d.SpectralEntropy = new float[n]; d.SpectralComplexity = new float[n];
                d.HpcpCrest = new float[n]; d.HpcpEntropy = new float[n]; d.Hfc = new float[n];
                d.BeatsLoudness = new float[n]; d.ChordsStrength = new float[n]; d.DynamicRange = new float[n];
                d.JsonOffset = new long[n]; d.JsonLength = new int[n];
                d.Sha = new byte[n][]; d.SpeechLikely = new byte[n];
                d.Genre = new string[n]; d.HasHpcp = new byte[n]; d.HasThpcp = new byte[n]; d.HasSmfm = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    d.PathHash[i] = br.ReadInt64();
                    d.RawBpm[i] = br.ReadSingle(); d.RawBpmAlt[i] = br.ReadSingle();
                    d.RawBpmAltWeight[i] = br.ReadSingle(); d.DynCx[i] = br.ReadSingle();
                    d.KeyCode[i] = br.ReadInt32(); d.KeyAgreement[i] = br.ReadInt16(); d.KeyVotesPresent[i] = br.ReadUInt16();
                    d.Centroid[i] = br.ReadSingle(); d.Flux[i] = br.ReadSingle(); d.Loudness[i] = br.ReadSingle(); d.Dance[i] = br.ReadSingle();
                    d.Onset[i] = br.ReadSingle(); d.Zcr[i] = br.ReadSingle(); d.Rms[i] = br.ReadSingle(); d.Flatness[i] = br.ReadSingle();
                    d.Dissonance[i] = br.ReadSingle(); d.PitchSalience[i] = br.ReadSingle(); d.ChordsChanges[i] = br.ReadSingle(); d.SmfmBpm[i] = br.ReadSingle();
                    ushort gid = br.ReadUInt16();
                    d.Genre[i] = gid != NoGenre && gid < genreTable.Length ? genreTable[gid] : null;
                    d.HasHpcp[i] = br.ReadByte(); d.HasThpcp[i] = br.ReadByte(); d.HasSmfm[i] = br.ReadByte();
                    d.SpectralSkewness[i] = br.ReadSingle(); d.SpectralEntropy[i] = br.ReadSingle(); d.SpectralComplexity[i] = br.ReadSingle();
                    d.HpcpCrest[i] = br.ReadSingle(); d.HpcpEntropy[i] = br.ReadSingle(); d.Hfc[i] = br.ReadSingle();
                    d.BeatsLoudness[i] = br.ReadSingle(); d.ChordsStrength[i] = br.ReadSingle(); d.DynamicRange[i] = br.ReadSingle();
                    d.JsonOffset[i] = br.ReadInt64(); d.JsonLength[i] = br.ReadInt32();
                    d.Sha[i] = br.ReadBytes(32); d.SpeechLikely[i] = br.ReadByte();
                }
                d.Chroma = new float[n * ChromaFloats];
                for (int i = 0; i < d.Chroma.Length; i++) d.Chroma[i] = br.ReadSingle();
                d.Smfm = new float[n * SmfmSlots];
                for (int i = 0; i < d.Smfm.Length; i++) d.Smfm[i] = br.ReadSingle();
                d.Paths = new string[n];
                for (int i = 0; i < n; i++) d.Paths[i] = ReadString(br);
                return d;
            }
        }
    }
}
