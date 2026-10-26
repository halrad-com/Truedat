using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Truedat.Store
{
    /// <summary>Merge winners use source order; absent values belong only to the same or unknown content.</summary>
    internal static class MergeSafetySelfTest
    {
        const string Root = @"D:\MergeSafety", PathA = Root + @"\a.flac", PathB = Root + @"\b.flac";
        const string Old = "2026-05-01T00:00:00.0000000Z", New = "2026-06-01T00:00:00.0000000Z";
        static readonly string ShaA = new string('a', 64), ShaB = new string('b', 64), Md5 = new string('c', 32);

        internal static void Run(Action<bool, string> assert)
        {
            Block(assert, "older winner", () => OlderWinner(assert));
            foreach (bool knownEntity in new[] { false, true })
                foreach (bool differentKind in new[] { false, true })
                    Block(assert, $"replacement ({knownEntity}, {differentKind})", () => Replacement(assert, knownEntity, differentKind));
            foreach (bool storedHash in new[] { false, true })
                foreach (bool winningHash in new[] { false, true })
                    Block(assert, $"NULL preservation ({storedHash}, {winningHash})", () => PreserveAbsent(assert, storedHash, winningHash));
            foreach (bool explicitNull in new[] { false, true })
                Block(assert, "ordinary import absent hash", () => OrdinaryImportAbsentHash(assert, explicitNull));
            foreach (bool explicitNull in new[] { false, true })
                Block(assert, "first mirror import absent hash", () => FirstMirrorImportAbsentHash(assert, explicitNull));
            foreach (string mode in new[] { "omitted", "NULL", "supplied zero/empty" })
                Block(assert, "ordinary import absent scalars", () => OrdinaryImportAbsentScalars(assert, mode));
            Block(assert, "fresh scan NULL", () => FreshScanNull(assert));
            Block(assert, "library cache hit NULL", () => LibraryCacheHitNull(assert));
            Block(assert, "SHA cache MD5", () => ShaCacheMd5(assert));
            Block(assert, "legacy alias", () => LegacyAlias(assert));
        }

        static void OlderWinner(Action<bool, string> assert)
        {
            using var f = new Fixture();
            f.Seed((PathA, Entry(ShaA, "flac-frames", New, true)));
            var winner = Entry(ShaA, "flac-frames", Old, false);
            winner["title"] = "Explicit later winner"; winner["bpm"] = 130;
            string before = f.Values(PathA), history = f.History();
            long generation = f.Generation();

            // Ordinary import still rejects an older observation. Merge has the operator's explicit source order instead.
            var ordinary = f.Import(winner);
            assert(ordinary.Stale == 1 && f.Values(PathA) == before && f.Generation() == generation,
                "MergeSafety: an ordinary older import remains stale and leaves stored values/generation unchanged");
            int exit = f.Merge(winner);
            assert(exit == 0 && f.Cell("p.title || '|' || c.bpm || '|' || l.last_modified_utc", PathA) == "Explicit later winner|1300|" + Old,
                "MergeSafety: the explicit later merge source wins supplied title/BPM even with an older file mtime");
            assert(f.Cell("p.album || '|' || p.fileMd5", PathA) == "Stored album|" + Md5,
                "MergeSafety: choosing an older merge winner still preserves its omitted same-content copy facts");
            assert(f.Generation() > generation && f.HasHistory(history),
                "MergeSafety: the winning edit advances generation and keeps earlier run IDs and payloads");
            string once = f.Values(PathA); generation = f.Generation();
            exit = f.Merge(winner);
            assert(exit == 0 && f.Values(PathA) == once && f.Generation() == generation,
                "MergeSafety: repeating the explicit older winner changes neither stored values nor generation");
        }

        static void Replacement(Action<bool, string> assert, bool knownEntity, bool differentKind)
        {
            string label = $"MergeSafety replacement ({(knownEntity ? "existing destination entity" : "new hash")}, {(differentKind ? "different hash kind" : "different hash")})";
            using var f = new Fixture();
            // A kind transition goes from legacy region to frame hashing. The reverse has a separate legacy-alias contract.
            string? oldKind = differentKind ? null : "flac-frames";
            string nextSha = differentKind ? ShaA : ShaB;
            var original = Entry(ShaA, oldKind, Old, true);
            var other = Entry(nextSha, "flac-frames", New, true);
            other["title"] = "Other copy"; other["fileMd5"] = new string('d', 32);
            if (knownEntity) f.Seed((PathA, original), (PathB, other)); else f.Seed((PathA, original));
            string history = f.History(), otherBefore = knownEntity ? f.CopyValues(PathB) : "";
            var winner = Entry(nextSha, "flac-frames", New, false);
            winner["title"] = "Replacement";
            int exit = f.Merge(winner);
            assert(exit == 0 && f.Cell("c.audioStreamSha256 || '|' || c.audioStreamSha256Source || '|' || p.title", PathA) == nextSha + "|flac-frames|Replacement",
                label + ": supplied content identity and title reach the destination");
            assert(f.Cell("(p.album IS NULL) || '|' || (p.fileMd5 IS NULL) || '|' || (p.smfmBpm IS NULL) || '|' || (p.smfmScores IS NULL) || '|' || (p.fingerprint_v1_audioHead64kMd5 IS NULL)", PathA) == "1|1|1|1|1",
                label + ": the replacement never inherits album, MD5, SMFM, or fingerprint from the previous content");
            assert(f.HasHistory(history) && f.Sql("PRAGMA foreign_key_check;") == null,
                label + ": previous run IDs/payloads remain and foreign keys are valid");
            if (knownEntity)
                assert(f.CopyValues(PathB) == otherBefore && f.Cell("l.entity_id", PathA) == f.Cell("l.entity_id", PathB),
                    label + ": joining a known entity does not borrow or rewrite its other copy's facts");
            string once = f.Values(PathA); long generation = f.Generation();
            exit = f.Merge(winner);
            assert(exit == 0 && f.Values(PathA) == once && f.Generation() == generation,
                label + ": repeat merge preserves the result and generation");
        }

        static void PreserveAbsent(Action<bool, string> assert, bool storedHash, bool winningHash)
        {
            string label = $"MergeSafety NULL preservation (stored hash {storedHash}, winning hash {winningHash})";
            using var f = new Fixture();
            f.Seed((PathA, Entry(storedHash ? ShaA : null, "flac-frames", Old, true)));
            // Mix explicit NULL with omitted fields. Both mean unobserved; supplied zero and empty are real values.
            var winner = Entry(winningHash ? ShaA : null, "flac-frames", New, false);
            winner["album"] = null; winner["fileMd5"] = null; winner["smfmScores"] = null;
            winner["mfcc"] = null; winner["title"] = ""; winner["bpm"] = 0;
            int exit = f.Merge(winner);
            assert(exit == 0 && f.Cell("quote(p.title) || '|' || c.bpm", PathA) == "''|0",
                label + ": supplied empty title and zero BPM replace stored values");
            assert(f.Cell("p.album || '|' || p.fileMd5 || '|' || p.smfmBpm || '|' || (p.smfmScores IS NOT NULL) || '|' || p.fingerprint_v1_audioHead64kMd5 || '|' || (c.mfcc IS NOT NULL)", PathA)
                == "Stored album|" + Md5 + "|95000000|1|" + Md5 + "|1",
                label + ": explicit NULL and omitted content/copy fields keep the known stored values");
            string once = f.Values(PathA); long generation = f.Generation();
            exit = f.Merge(winner);
            assert(exit == 0 && f.Values(PathA) == once && f.Generation() == generation,
                label + ": repeated partial winner leaves values and generation unchanged");
        }

        // These exercise ordinary import, not merge's source-order/presence reconciliation.
        // A missing hash must not demote a known description or turn absent measurements into a replacement row.
        static void OrdinaryImportAbsentHash(Action<bool, string> assert, bool explicitNull)
        {
            string label = "MergeSafety ordinary import absent hash (" + (explicitNull ? "NULL" : "omitted") + ")";
            using var f = new Fixture();
            f.Seed((PathA, Entry(ShaA, "flac-frames", Old, true)));
            string history = f.History();
            var partial = new Dictionary<string, object?>
            {
                ["lastModified"] = New, ["title"] = "Partial metadata", ["bpm"] = 120,
            };
            if (explicitNull) { partial["audioStreamSha256"] = null; partial["mfcc"] = null; }
            var report = f.Import(partial);
            string? identity = f.Cell("c.content_key || '|' || c.content_key_kind || '|' || coalesce(c.audioStreamSha256, '<null>')", PathA);
            assert(identity == ShaA + "|content_hash:audio-stream-sha256|" + ShaA,
                label + ": absent SHA keeps the known description identity (got " + identity + ")");
            string? mfcc = f.Cell("hex(c.mfcc)", PathA);
            assert(mfcc == "983A00001CA8FFFF",
                label + ": absent MFCC keeps the stored measurement (got " + mfcc + ")");
            assert(report.Stale == 0 && f.Cell("p.title || '|' || p.album || '|' || p.fileMd5", PathA)
                == "Partial metadata|Stored album|" + Md5,
                label + ": supplied title lands while omitted copy facts survive");
            assert(f.HasHistory(history) && f.Sql("PRAGMA foreign_key_check;") == null,
                label + ": earlier run IDs/payloads survive and foreign keys remain valid");
        }

        // A first mirror location can match a primary entity by relative path without carrying a hash. Its absent values must not
        // re-key the SHARED description to entity:<id> and erase the primary's measurements (the location itself has no prior key).
        static void FirstMirrorImportAbsentHash(Action<bool, string> assert, bool explicitNull)
        {
            string label = "MergeSafety first mirror import absent hash (" + (explicitNull ? "NULL" : "omitted") + ")";
            const string mirrorRoot = @"X:\MirrorSafety", mirrorPath = mirrorRoot + @"\a.flac";
            using var f = new Fixture();
            var primary = Entry(ShaA, "flac-frames", Old, true); primary["key"] = "C";
            f.Seed((PathA, primary), (PathB, Entry(ShaB, "flac-frames", Old, true)));
            const string content = "quote(c.content_key) || '|' || quote(c.content_key_kind) || '|' || quote(c.audioStreamSha256) || '|' || quote(c.audioStreamSha256Source) || '|' || quote(c.bpm) || '|' || quote(c.key) || '|' || hex(c.mfcc) || '|' || quote(c.run_id)";
            string before = f.Cell(content, PathA)!, copy = f.CopyValues(PathA), neighbor = f.Values(PathB), history = f.History();
            string? entity = f.Cell("l.entity_id", PathA);
            var partial = new Dictionary<string, object?> { ["lastModified"] = New, ["title"] = "Mirror metadata" };
            if (explicitNull)
                foreach (string field in new[] { "audioStreamSha256", "audioStreamSha256Source", "bpm", "key", "mfcc", "album", "fileMd5" }) partial[field] = null;
            var report = f.ImportMirror(mirrorRoot, mirrorPath, partial);
            assert(report.MatchedPath == 1 && report.EntitiesCreated == 0 && report.CreatedNoSha == 0
                && f.Sql("SELECT count(*) FROM file_entity;") == "2" && f.Cell("l.entity_id", PathA) == entity && f.Cell("l.entity_id", mirrorPath) == entity,
                label + ": the new mirror location deliberately shares the primary entity without creating one");
            string? after = f.Cell(content, PathA);
            assert(after == before && f.Cell(content, mirrorPath) == before,
                label + ": absent identity and measurements preserve the primary's shared description and measuring run (got " + after + ")");
            assert(f.CopyValues(PathA) == copy && f.Cell("p.title || '|' || (p.album IS NULL) || '|' || (p.fileMd5 IS NULL)", mirrorPath) == "Mirror metadata|1|1",
                label + ": mirror metadata stays on its own copy and never borrows or rewrites the primary's copy facts");
            assert(f.Values(PathB) == neighbor && f.HasHistory(history) && f.Sql("PRAGMA foreign_key_check;") == null,
                label + ": the unrelated track and earlier history survive with valid foreign keys");
            string once = f.Values(PathA), mirrorOnce = f.Values(mirrorPath); long generation = f.Generation();
            f.ImportMirror(mirrorRoot, mirrorPath, partial);
            assert(f.Values(PathA) == once && f.Values(mirrorPath) == mirrorOnce && f.Generation() == generation && f.HasHistory(history),
                label + ": repeating the mirror import changes neither shared values, copy facts, nor generation");
        }

        // The reader maps missing/null nonnullable scalars to defaults. Presence, not those defaults,
        // decides what an ordinary import supplies. Explicit zero and empty string must still land.
        static void OrdinaryImportAbsentScalars(Action<bool, string> assert, string mode)
        {
            string label = "MergeSafety ordinary import scalars (" + mode + ")";
            using var f = new Fixture();
            var measured = new TrackEntry
            {
                LastModified = DateTime.Parse(Old, null, System.Globalization.DateTimeStyles.RoundtripKind),
                AudioStreamSha256 = ShaA, AudioStreamSha256Source = "flac-frames",
                Features = new TrackFeatures { Title = "Measured title", Bpm = 120, Key = "C", Mfcc = new[] { 1.5, -2.25 } },
            };
            // Synthetic measurements through the real scan writer; no audio/extractor dependency.
            ImportCommand.ExecuteEntries(f.Store, new Dictionary<string, TrackEntry>(PathComparer.Instance) { [PathA] = measured },
                Root, "primary", true, "essentia", "merge-safety", "synthetic scalar observation", null, null);
            assert(f.Cell("c.bpm || '|' || quote(c.key) || '|' || hex(c.mfcc)", PathA) == "1200|'C'|983A00001CA8FFFF",
                label + ": fixture starts with measured BPM, key, and MFCC");
            string history = f.History();
            var partial = new Dictionary<string, object?>
            {
                ["lastModified"] = New, ["title"] = "Partial metadata",
                ["audioStreamSha256"] = ShaA, ["audioStreamSha256Source"] = "flac-frames",
            };
            if (mode == "NULL") { partial["bpm"] = null; partial["key"] = null; partial["mfcc"] = null; }
            if (mode == "supplied zero/empty") { partial["bpm"] = 0; partial["key"] = ""; }
            f.Import(partial);
            string? scalars = f.Cell("c.bpm || '|' || quote(c.key)", PathA);
            string expected = mode == "supplied zero/empty" ? "0|''" : "1200|'C'";
            assert(scalars == expected,
                label + ": only supplied scalar values replace stored BPM/key (got " + scalars + ", expected " + expected + ")");
            assert(f.Cell("c.audioStreamSha256 || '|' || hex(c.mfcc) || '|' || p.title", PathA) == ShaA + "|983A00001CA8FFFF|Partial metadata",
                label + ": omitted measurements survive and supplied title lands");
            assert(f.HasHistory(history) && f.Sql("PRAGMA foreign_key_check;") == null,
                label + ": earlier run IDs/payloads survive and foreign keys remain valid");
        }

        static void LegacyAlias(Action<bool, string> assert)
        {
            using var f = new Fixture();
            f.Seed((PathA, Entry(ShaA, "flac-frames", New, true)));
            string content = f.Cell("c.content_key || '|' || c.content_key_kind || '|' || c.bpm || '|' || c.run_id", PathA)!;
            var alias = Entry(ShaB, null, Old, true);
            alias["title"] = "Legacy title"; alias["fileMd5"] = new string('e', 32); alias["genre"] = "Jazz"; alias["bpm"] = 77;
            var report = f.Import(alias);
            assert(report.ContentRekeyed == 0 && report.ContentReplaced == 0
                && f.Cell("c.content_key || '|' || c.content_key_kind || '|' || c.bpm || '|' || c.run_id", PathA) == content,
                "MergeSafety legacy alias: an ordinary old FLAC-region arrival does not replace the frame-keyed description or its run");
            assert(f.Cell("p.title || '|' || p.fileMd5 || '|' || p.genre", PathA) == "Stored title|" + Md5 + "|Jazz",
                "MergeSafety legacy alias: known older copy facts only fill missing fields; stored title and MD5 remain");
            int exit = f.Merge(alias);
            assert(exit == 0 && f.Cell("c.content_key || '|' || c.content_key_kind || '|' || c.bpm || '|' || c.run_id", PathA) == content
                && f.Cell("p.title || '|' || p.fileMd5", PathA) == "Stored title|" + Md5,
                "MergeSafety legacy alias: merge also preserves the existing legacy-alias contract");
        }

        // NULL is not present (operator 2026-10-25, the rule that holds for EVERY arrival): a scan's NULL copy fact never clears a stored
        // observation — a scan without --file-md5 did not look at the MD5, a scan with the SMFM reader off did not look at SMFM. The
        // one case where the scan KNOWS a stored fact is wrong (the whole-file MD5 after a tag-only edit) is an explicit removal,
        // CopyCleared, not a NULL. 5b9b8f3 made every scan NULL clear to get that one case; this replaces it.
        static void FreshScanNull(Action<bool, string> assert)
        {
            using (var f = new Fixture())
            {
                f.Seed((PathA, Entry(ShaA, "flac-frames", Old, true)));
                int exit = f.StripSmfm();
                assert(exit == 0 && f.Cell("(p.smfmBpm IS NULL) || '|' || (p.smfmScores IS NULL) || '|' || p.fileMd5", PathA) == "1|1|" + Md5,
                    "MergeSafety fresh scan: an explicit strip still clears SMFM and retains unrelated MD5");
            }
            foreach (ulong? mask in new ulong?[] { null, TrackEntryMapper.PerFileScanCopyObserved })
            {
                using var f = new Fixture();
                f.Seed((PathA, Entry(ShaA, "flac-frames", Old, true)));
                string mode = mask.HasValue ? "per-file mask" : "raw entry";
                var measured = new TrackEntry
                {
                    LastModified = DateTime.Parse(New, null, System.Globalization.DateTimeStyles.RoundtripKind),
                    AudioStreamSha256 = ShaA, AudioStreamSha256Source = "flac-frames",
                    Features = new TrackFeatures { Title = "Freshly measured " + mode, Bpm = 130 },
                    CopyObserved = mask,
                };
                ImportCommand.ExecuteEntries(f.Store, new Dictionary<string, TrackEntry>(PathComparer.Instance) { [PathA] = measured },
                    Root, "primary", true, "essentia", "merge-safety", "synthetic fresh scan", null, null);
                assert(f.Cell("p.title || '|' || c.bpm", PathA) == measured.Features.Title + "|1300",
                    "MergeSafety fresh scan (" + mode + "): the ordinary scan writer stores supplied title and BPM");
                string? facts = f.Cell("coalesce(p.fileMd5, '<null>') || '|' || coalesce(p.smfmBpm, '<null>') || '|' || (p.smfmScores IS NULL) || '|' || coalesce(p.fingerprint_v1_audioHead64kMd5, '<null>')", PathA);
                assert(facts == Md5 + "|95000000|0|" + Md5,
                    "MergeSafety fresh scan (" + mode + "): the scan's NULL keeps the stored MD5, SMFM and fingerprint — NULL is not present (got " + facts + ")");
                var invalidating = new TrackEntry
                {
                    LastModified = DateTime.Parse(New, null, System.Globalization.DateTimeStyles.RoundtripKind),
                    AudioStreamSha256 = ShaA, AudioStreamSha256Source = "flac-frames",
                    Features = new TrackFeatures { Title = "Freshly measured " + mode, Bpm = 130 },
                    CopyObserved = mask, CopyCleared = TrackEntryMapper.CopyMaskOf("fileMd5"),
                };
                ImportCommand.ExecuteEntries(f.Store, new Dictionary<string, TrackEntry>(PathComparer.Instance) { [PathA] = invalidating },
                    Root, "primary", true, "essentia", "merge-safety", "synthetic fresh scan", null, null);
                facts = f.Cell("coalesce(p.fileMd5, '<null>') || '|' || coalesce(p.smfmBpm, '<null>') || '|' || (p.smfmScores IS NULL) || '|' || coalesce(p.fingerprint_v1_audioHead64kMd5, '<null>')", PathA);
                assert(facts == "<null>|95000000|0|" + Md5,
                    "MergeSafety fresh scan (" + mode + "): an explicit clear removes exactly the MD5 and nothing else (got " + facts + ")");
            }
        }

        // The library scan's cache-hit tiers rebuild the catalog entry WITHOUT analysing (Program.cs quick-head tier: a tag-only edit
        // keeps the analysis, sets FileMd5 = null because the whole-file digest is stale AND marks it CopyCleared) and hand it to
        // ScanSink.RecordCacheHit, which flushes under the import producer. The explicit clear is what removes the stale digest, so
        // the next publish cannot write it back (codex 2026-10-24 P2); a rebuilt entry carrying a plain NULL keeps the stored MD5.
        static void LibraryCacheHitNull(Action<bool, string> assert)
        {
            using var f = new Fixture();
            f.Seed((PathA, Entry(ShaA, "flac-frames", Old, true)));
            var previousDir = ScanSink.StoreDirectory;
            try
            {
                ScanSink.Reset(); ScanSink.StoreDirectory = System.IO.Path.GetDirectoryName(f.Store);
                var rebuilt = new TrackEntry
                {
                    LastModified = DateTime.Parse(New, null, System.Globalization.DateTimeStyles.RoundtripKind),
                    AudioStreamSha256 = ShaA, AudioStreamSha256Source = "flac-frames",
                    Features = new TrackFeatures { Title = "Tags edited", Bpm = 120 },
                    FileMd5 = null,   // a plain NULL: not present
                };
                ScanSink.RecordCacheHit(PathA, rebuilt);
                var flush = ScanSink.Flush(f.Store, "merge-safety", null, final: true);
                string? facts = f.Cell("coalesce(p.fileMd5, '<null>') || '|' || p.title", PathA);
                assert(flush.Ok && facts == Md5 + "|Tags edited",
                    "MergeSafety library cache hit: a rebuilt entry's NULL MD5 keeps the stored one — NULL is not present (got " + facts + ", flush ok " + flush.Ok + ")");
                ScanSink.Reset(); ScanSink.StoreDirectory = System.IO.Path.GetDirectoryName(f.Store);
                var invalidated = new TrackEntry
                {
                    LastModified = DateTime.Parse(New, null, System.Globalization.DateTimeStyles.RoundtripKind).AddMinutes(1),
                    AudioStreamSha256 = ShaA, AudioStreamSha256Source = "flac-frames",
                    Features = new TrackFeatures { Title = "Tags edited again", Bpm = 120 },
                    FileMd5 = null, CopyCleared = TrackEntryMapper.CopyMaskOf("fileMd5"),   // the quick-head tier's invalidation
                };
                ScanSink.RecordCacheHit(PathA, invalidated);
                flush = ScanSink.Flush(f.Store, "merge-safety", null, final: true);
                facts = f.Cell("coalesce(p.fileMd5, '<null>') || '|' || p.title", PathA);
                assert(flush.Ok && facts == "<null>|Tags edited again",
                    "MergeSafety library cache hit: the MD5 the quick-head tier invalidated leaves the store through the cache-hit flush (got " + facts + ", flush ok " + flush.Ok + ")");
            }
            finally { ScanSink.Reset(); ScanSink.StoreDirectory = previousDir; }
        }

        // Exercise the same builder -> MD5 rule -> cache flush used after a tag-edited file hits the audio-SHA cache.
        // The helper alone can miss an old MD5 that the builder copied forward when no new MD5 was requested.
        static void ShaCacheMd5(Action<bool, string> assert)
        {
            var rebuild = typeof(Program).GetMethod("RebuildCacheEntryCore", BindingFlags.NonPublic | BindingFlags.Static)!;
            bool wasMd5 = Program._fileMd5Enabled;
            string? previousDir = ScanSink.StoreDirectory;
            try
            {
                foreach (bool measureMd5 in new[] { false, true })
                {
                    using var f = new Fixture();
                    f.Seed((PathA, Entry(ShaA, "flac-frames", Old, true)));
                    var stored = StoreDictionary.Load(f.Store, null, out _)[PathA];
                    string history = f.History(), freshMd5 = new string('d', 32);
                    Program._fileMd5Enabled = measureMd5;
                    var rebuilt = (TrackEntry)rebuild.Invoke(null, new object?[]
                    {
                        stored, 0, "Artist", "Tags edited", "Stored album", "Genre", PathA,
                        DateTime.Parse(New, null, System.Globalization.DateTimeStyles.RoundtripKind),
                        measureMd5 ? freshMd5 : null, null, ShaA, "flac-frames",
                    })!;
                    Program.RefreshedMd5Rule(rebuilt, stored.FileMd5);
                    ScanSink.Reset(); ScanSink.StoreDirectory = System.IO.Path.GetDirectoryName(f.Store);
                    ScanSink.RecordCacheHit(PathA, rebuilt);
                    var flush = ScanSink.Flush(f.Store, "merge-safety", null, final: true);
                    string label = "MergeSafety SHA cache (" + (measureMd5 ? "with --file-md5" : "without --file-md5") + ")";
                    string? md5 = f.Cell("p.fileMd5", PathA);
                    assert(flush.Ok && md5 == (measureMd5 ? freshMd5 : null),
                        label + ": the changed file never retains its old whole-file MD5 (got " + (md5 ?? "<null>") + ")");
                    assert(f.Cell("p.title || '|' || c.audioStreamSha256 || '|' || p.smfmBpm || '|' || hex(c.mfcc)", PathA)
                        == "Tags edited|" + ShaA + "|95000000|983A00001CA8FFFF",
                        label + ": supplied tags land while audio identity, SMFM, and MFCC survive");
                    assert(f.HasHistory(history) && f.Sql("PRAGMA foreign_key_check;") == null,
                        label + ": prior run metadata survives and foreign keys remain valid");
                }
            }
            finally { Program._fileMd5Enabled = wasMd5; ScanSink.Reset(); ScanSink.StoreDirectory = previousDir; }
        }

        static Dictionary<string, object?> Entry(string? sha, string? kind, string modified, bool full)
        {
            var e = new Dictionary<string, object?>
            {
                ["lastModified"] = modified, ["title"] = "Stored title", ["bpm"] = 120,
            };
            if (sha != null) e["audioStreamSha256"] = sha;
            if (kind != null) e["audioStreamSha256Source"] = kind;
            if (full)
            {
                e["album"] = "Stored album"; e["fileMd5"] = Md5; e["smfmBpm"] = 95;
                e["smfmScores"] = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }; e["mfcc"] = new[] { 1.5, -2.25 };
                e["fingerprint.v1"] = new Dictionary<string, object?>
                {
                    ["fileSize"] = 100000, ["durationMs"] = 1000, ["sampleRate"] = 44100, ["channels"] = 2,
                    ["codec"] = "flac", ["pathTail"] = "a.flac", ["audioHead64kMd5"] = Md5,
                };
            }
            return e;
        }

        static void Block(Action<bool, string> assert, string label, Action test)
        {
            try { test(); }
            catch (Exception ex) { assert(false, "MergeSafety " + label + ": " + ex); }
        }

        sealed class Fixture : IDisposable
        {
            static readonly FieldInfo StoreField = typeof(Program).GetField("_storePath", BindingFlags.NonPublic | BindingFlags.Static)!;
            static readonly MethodInfo MergeMethod = typeof(Program).GetMethod("RunMergeMoods", BindingFlags.NonPublic | BindingFlags.Static)!;
            readonly string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".truedat-selftest", "merge-safety-" + Guid.NewGuid().ToString("N"));
            readonly object? savedStore = StoreField.GetValue(null);
            readonly IDisposable quiet = StoreLog.Quiet();
            readonly int savedExit = Environment.ExitCode;
            internal string Store => System.IO.Path.Combine(dir, "truedat.db");
            string Catalog => System.IO.Path.Combine(dir, "mbxmoods.json");
            internal Fixture() { Directory.CreateDirectory(dir); }

            internal void Seed(params (string Path, Dictionary<string, object?> Entry)[] entries)
            {
                string file = Write("seed.json", entries);
                ImportCommand.Execute(new ImportOptions { StorePath = Store, CatalogPath = file, LibraryRoot = Root });
            }
            internal ImportReport Import(Dictionary<string, object?> entry)
                => ImportCommand.Execute(new ImportOptions { StorePath = Store, CatalogPath = Write("import.json", (PathA, entry)), LibraryRoot = Root });
            internal ImportReport ImportMirror(string root, string path, Dictionary<string, object?> entry)
                => ImportCommand.Execute(new ImportOptions { StorePath = Store, CatalogPath = Write("mirror.json", (path, entry)), LibraryRoot = root, RootRole = "mirror" });
            internal int Merge(Dictionary<string, object?> entry)
            {
                string winner = Write("winner.json", (PathA, entry));
                return Verb("merge-moods", "--merge-moods",
                    () => (int)MergeMethod.Invoke(null, new object[] { new List<string> { Catalog, winner }, Catalog })!);
            }
            internal int StripSmfm() => Verb("strip", "--strip-smfm", () =>
            {
                typeof(Program).GetMethod("RunStripSmfm", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { Catalog, false });
                return Environment.ExitCode;
            });
            int Verb(string verb, string flag, Func<int> run)
            {
                StoreField.SetValue(null, Store);
                var output = Console.Out;
                try
                {
                    Console.SetOut(TextWriter.Null);
                    return Program.RunStoreBackedVerb(verb, flag, Catalog, run);
                }
                finally { Console.SetOut(output); }
            }
            string Write(string name, params (string Path, Dictionary<string, object?> Entry)[] entries)
            {
                var tracks = new Dictionary<string, object?>();
                foreach (var e in entries) tracks[e.Path] = e.Entry;
                string file = System.IO.Path.Combine(dir, name);
                File.WriteAllText(file, JsonSerializer.Serialize(new Dictionary<string, object?> { ["tracks"] = tracks, ["trackCount"] = tracks.Count }));
                return file;
            }
            internal string? Sql(string sql)
            {
                using var os = OpenStore.Open(Store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
                return os.Db.ExecScalar(sql);
            }
            internal string? Cell(string expression, string path)
                => Sql("SELECT " + expression + " FROM track_location l JOIN track_content c ON c.entity_id = l.entity_id JOIN track_copy p ON p.location_id = l.id WHERE l.display_path = '" + path.Replace("'", "''") + "';");
            internal long Generation() => long.Parse(Sql("SELECT value FROM store_meta WHERE key = 'generation';")!);
            internal string CopyValues(string path)
                => Cell("quote(p.title) || '|' || quote(p.album) || '|' || quote(p.fileMd5) || '|' || quote(p.smfmBpm) || '|' || quote(p.smfmScores) || '|' || quote(p.fingerprint_v1_audioHead64kMd5) || '|' || quote(p.run_id) || '|' || quote(p.updated_utc)", path)!;
            internal string Values(string path)
                => CopyValues(path) + "|" + Cell("quote(c.audioStreamSha256) || '|' || quote(c.audioStreamSha256Source) || '|' || quote(c.bpm) || '|' || quote(c.mfcc) || '|' || quote(c.run_id) || '|' || quote(l.last_modified_utc)", path);
            internal string History() => Sql("SELECT group_concat(row, char(10)) FROM (SELECT id || '|' || quote(producer_key) || '|' || quote(content_hash) || '|' || quote(location_id) || '|' || quote(source) AS row FROM run ORDER BY id);") ?? "";
            internal bool HasHistory(string before)
            {
                string after = "\n" + History() + "\n";
                foreach (string row in before.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (!after.Contains("\n" + row + "\n")) return false;
                return true;
            }
            public void Dispose()
            {
                StoreField.SetValue(null, savedStore); Environment.ExitCode = savedExit; quiet.Dispose();
                string ownedRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".truedat-selftest")) + System.IO.Path.DirectorySeparatorChar;
                if (System.IO.Path.GetFullPath(dir).StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
