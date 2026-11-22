using System;
using System.Collections.Generic;
using System.Linq;

namespace Truedat
{
    /// <summary>The two things a scan fits to the machine and the library: the RAM its extractors share for decoded audio
    /// (<see cref="SpoolBudget"/>) and the order it takes tracks in (<see cref="LongFirstQueue{T}"/>).</summary>
    internal static class ScanTuningSelfTest
    {
        const long GiB = 1L << 30, MiB = 1L << 20;
        const long Cd = 44100L * 2 * 4;   // bytes one second of 44.1 kHz stereo decodes to

        internal static void Run(Action<bool, string> assert)
        {
            Budget(assert);
            Order(assert);
            Repeats(assert);
            OtherCopy(assert);
        }
        /// <summary>Review #5. When a track's audio is already in the catalog under another path, the acoustic features are reused
        /// and everything that belongs to the copy comes from the copy: its own text, its own SMFM block, its own place in the
        /// library. The donor's are another file's.</summary>
        static void OtherCopy(Action<bool, string> assert)
        {
            var donor = new TrackEntry
            {
                Features = new TrackFeatures
                {
                    TrackId = 11, Artist = "Donor Artist", Title = "Donor Title", Album = "Donor Album", Genre = "Donor Genre",
                    FilePath = @"C:\lib\a.flac", Bpm = 120.5, Key = "A", Mode = "minor",
                    SmfmScores = new[] { 1, 2, 3 }, SmfmChannel = 2, SmfmChannelName = null, SmfmBpm = 119.9,
                },
                AudioStreamSha256 = "aa", FileMd5 = "donor-md5", LastModified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            var recipient = new ITunesTrack
            {
                TrackId = 22, Artist = "Xml Artist", Name = "Xml Title", Album = "Xml Album", Genre = "Xml Genre",
                Location = @"C:\lib\b.flac",
            };
            // What a fresh analysis of this copy would write: the library's text and number for it - never the other copy's,
            // whether or not the file was written after the XML.
            var reused = Program.RebuildFromAnotherCopy(donor, recipient, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                readSmfm: null, refreshedFileMd5: "own-md5", refreshedFp: null);
            var prev = Program.LibraryXmlWrittenUtc;
            Program.LibraryXmlWrittenUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);   // the file is newer than the XML
            var reusedNewer = Program.RebuildFromAnotherCopy(donor, recipient, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                readSmfm: null, refreshedFileMd5: "own-md5", refreshedFp: null);
            Program.LibraryXmlWrittenUtc = prev;
            foreach (var e in new[] { reused, reusedNewer })
                assert(e.Features.Title == "Xml Title" && e.Features.Artist == "Xml Artist" && e.Features.Album == "Xml Album"
                    && e.Features.Genre == "Xml Genre",
                    $"another copy: the copy's own text from the library, never the other copy's (got '{e.Features.Artist} - {e.Features.Title} / {e.Features.Album}')");
            var newer = reusedNewer;
            assert(newer.Features.TrackId == 22, "another copy: the library's own number for the copy, not the other copy's");
            assert(newer.Features.FilePath == @"C:\lib\b.flac", "another copy: the entry names the copy's own path");

            // SMFM is the copy's: a copy with none has none, a copy with its own has its own.
            assert(newer.Features.SmfmScores == null && newer.Features.SmfmChannel == null && newer.Features.SmfmBpm == null,
                "another copy: an untagged copy does not carry the other copy's SMFM block");
            var ownSmfm = Program.RebuildFromAnotherCopy(donor, recipient, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                readSmfm: f => { f.SmfmScores = new[] { 9, 9 }; f.SmfmChannel = 0; f.SmfmBpm = 88.0; },
                refreshedFileMd5: null, refreshedFp: null);
            assert(ownSmfm.Features.SmfmScores != null && ownSmfm.Features.SmfmScores.Length == 2 && ownSmfm.Features.SmfmScores[0] == 9
                && ownSmfm.Features.SmfmBpm == 88.0,
                "another copy: a tagged copy carries its OWN SMFM block");

            // What the copies share is kept: the analysis of the audio.
            assert(newer.Features.Bpm == 120.5 && newer.Features.Key == "A" && newer.Features.Mode == "minor",
                "another copy: the analysis of the shared audio is reused");
            assert(newer.FileMd5 == "own-md5", "another copy: the copy's own file MD5");
            assert(donor.Features.Title == "Donor Title" && donor.Features.SmfmScores != null,
                "another copy: the other copy's entry is left as it was");
        }

        /// <summary>Tracks that may be the same audio as one before them wait for a second pass, by which time the first has been
        /// analyzed and the same-audio cache tier can reuse it. The test for "may be the same" is an exact length: two copies of the
        /// same audio are always the same length, and the library already says how long each track is.</summary>
        static void Repeats(Action<bool, string> assert)
        {
            //           0     1     2     3     4   5     6     7
            var ms = new long[] { 1000, 2000, 1000, 3000, 0, 2000, 1000, 0 };
            var idx = Enumerable.Range(0, ms.Length).ToList();
            ScanOrder.SplitRepeatedLengths(idx, i => ms[i], out var first, out var again);
            assert(first.SequenceEqual(new[] { 0, 1, 3, 4, 7 }), $"second pass: the first track of each length is analyzed in the first pass, in library order ({string.Join(",", first)})");
            assert(again.SequenceEqual(new[] { 2, 5, 6 }), $"second pass: a later track of the same exact length waits, in library order ({string.Join(",", again)})");
            assert(first.Contains(4) && first.Contains(7), "second pass: a track of unknown length never waits — it cannot be matched to anything");
            assert(first.Count + again.Count == ms.Length && !first.Intersect(again).Any(), "second pass: every track is in exactly one pass");
            ScanOrder.SplitRepeatedLengths(new List<int>(), i => 0, out var f0, out var a0);
            assert(f0.Count == 0 && a0.Count == 0, "second pass: nothing in, nothing out");
            ScanOrder.SplitRepeatedLengths(new List<int> { 0, 1, 3 }, i => ms[i], out var f1, out var a1);
            assert(f1.Count == 3 && a1.Count == 0, "second pass: a library with no repeated length has no second pass");
        }

        static void Budget(Action<bool, string> assert)
        {
            var b = new SpoolBudget(10 * GiB);
            assert(b.TotalBytes == 6 * GiB, $"spool budget: the extractors share 60% of the free RAM ({b.TotalBytes / (double)GiB:F2} GiB of 10)");

            var g = b.Reserve(300, Cd);
            assert(g.SpoolBytes >= 300 * Cd && g.SpoolBytes <= 300 * Cd * 11 / 10 + 16 * MiB,
                $"spool budget: a track is given room for its own decoded audio and a margin, no more ({g.SpoolBytes / (double)MiB:F1} MiB for {300 * Cd / (double)MiB:F1} MiB of audio)");
            assert(b.ReservedBytes == g.SpoolBytes + g.ExtractorBytes && g.ExtractorBytes == SpoolBudget.ExtractorBytesFor(300),
                "spool budget: what a track holds is its audio plus the extractor's own memory for that length");
            b.Release(g);
            assert(b.ReservedBytes == 0, $"spool budget: a finished track gives all of it back ({b.ReservedBytes} bytes still held)");

            // First to ask wins: the room is given whole or not at all, and a track that does not get it still runs.
            var small = new SpoolBudget(6 * GiB);                                    // 3.6 GiB to share
            var first = small.Reserve(2 * 3600, Cd);                                 // two hours: about 2.4 GiB of audio
            var shortOne = small.Reserve(240, Cd);
            assert(first.SpoolBytes > 2 * GiB && shortOne.SpoolBytes > 0,
                $"spool budget: a short track still fits beside a long one that took most of the room (long {first.SpoolBytes / (double)GiB:F2} GiB, short {shortOne.SpoolBytes / (double)MiB:F0} MiB)");
            var second = small.Reserve(2 * 3600, Cd);
            assert(second.SpoolBytes == 0 && second.ExtractorBytes > 0,
                $"spool budget: first to ask wins, and the one that is refused is still counted as running (second {second.SpoolBytes})");
            small.Release(first);
            small.Release(second);
            var next = small.Reserve(2 * 3600, Cd);
            assert(next.SpoolBytes > 2 * GiB, "spool budget: the room a long track gives back goes to the next one that asks");

            // A long track on a large machine is not held to one worker's share.
            var big = new SpoolBudget(120 * GiB);
            var others = Enumerable.Range(0, 29).Select(_ => big.Reserve(270, Cd)).ToList();
            var threeHours = big.Reserve(3 * 3600, Cd);
            assert(others.All(o => o.SpoolBytes > 0) && threeHours.SpoolBytes > 3 * GiB + GiB / 2,
                $"spool budget: with 29 ordinary tracks running, a three-hour track is held whole on a large machine ({threeHours.SpoolBytes / (double)GiB:F2} GiB)");
            assert(big.ReservedBytes <= big.TotalBytes, "spool budget: what is granted never passes the budget");

            // The smallest machine it has to work on: 8 GiB free, 20 workers, ordinary tracks.
            var floor = new SpoolBudget(8 * GiB);
            var twenty = Enumerable.Range(0, 20).Select(_ => floor.Reserve(300, Cd)).ToList();
            assert(twenty.All(t => t.SpoolBytes > 0) && floor.ReservedBytes <= floor.TotalBytes,
                $"spool budget: 20 five-minute tracks all fit in 8 GiB free ({twenty.Count(t => t.SpoolBytes > 0)} of 20 held, {floor.ReservedBytes / (double)MiB:F0} of {floor.TotalBytes / (double)MiB:F0} MiB)");
            var hour = floor.Reserve(3600, Cd);
            assert(hour.SpoolBytes == 0, "spool budget: and an hour-long track is not held there while those 20 are running");

            assert(small.Held == 3 && small.Refused == 1 && floor.Held == 20 && floor.Refused == 1,
                $"spool budget: the run's account counts who was given room and who was refused (small {small.Held}/{small.Refused}, floor {floor.Held}/{floor.Refused})");
            var acct = new SpoolBudget(8 * GiB);
            assert(acct.SummaryLine() == null, "spool budget: a run in which no track asked says nothing");
            acct.Release(acct.Reserve(300, Cd)); acct.Reserve(0, Cd);
            assert(acct.SummaryLine() == "1 track(s) held in memory for analysis, 1 of unreadable length  (4.8 GB shared)",
                $"spool budget: the summary line ('{acct.SummaryLine()}')");
            assert(new SpoolBudget(8 * GiB).Reserve(0, Cd).SpoolBytes == 0 && new SpoolBudget(8 * GiB).Reserve(300, 0).SpoolBytes == 0,
                "spool budget: a track whose length or format is unknown is not held in memory");
            assert(new SpoolBudget(0).Reserve(300, Cd).SpoolBytes == 0 && new SpoolBudget(-1).TotalBytes == 0,
                "spool budget: with no reading of free RAM nothing is held in memory");

            // Review #7: the extractor refuses to hold a track when its OWN estimate - length x rate x channels x 4 bytes, plus 5%
            // (audioloader.cpp, startRecording) - is over the cap it was handed. A grant smaller than that is counted here as a track
            // held in memory while the extractor decodes it once per pass. The grant must cover the extractor's estimate, for a
            // length read from the file's header that may be a little shorter than the one the extractor reads.
            foreach (var secs in new double[] { 30, 300, 794, 840, 1800, 3600, 4 * 3600 })
            {
                var grant = new SpoolBudget(120 * GiB).Reserve(secs, Cd);
                double extractorWants = secs * 1.01 * Cd * 1.05;
                assert(grant.SpoolBytes >= extractorWants,
                    $"spool budget: the grant covers the extractor's own check at {secs:F0} s, with the length up to 1% longer than the header said ({grant.SpoolBytes:N0} >= {extractorWants:N0})");
            }
            assert(new SpoolBudget(120 * GiB).Reserve(840, Cd).SpoolBytes >= 311169600,
                "spool budget: the review's 14-minute 44.1 kHz stereo track gets at least the 311,169,600 bytes the extractor asked for");

            // Review #12: every worker's extractor runs whether or not its track is held in memory, so a grant must leave room for the
            // extractors of the workers that have not started yet. Without that, early tracks take the room and later extractors push
            // what is reserved over the budget.
            // A list or folder scan knows no lengths, so it runs with the default typical length (codex, after e5827df).
            var twelveUnknown = new SpoolBudget(8 * GiB, workers: 20);
            for (int i = 0; i < 20; i++) twelveUnknown.Reserve(12 * 60, Cd);
            assert(twelveUnknown.ReservedBytes <= twelveUnknown.TotalBytes,
                $"spool budget: 20 workers on 12-minute tracks in 8 GiB stay inside the budget when the run knows no typical length ({twelveUnknown.ReservedBytes:N0} of {twelveUnknown.TotalBytes:N0})");
            var twelve = new SpoolBudget(8 * GiB, workers: 20, typicalSeconds: 12 * 60);   // a library of 12-minute tracks
            for (int i = 0; i < 20; i++) twelve.Reserve(12 * 60, Cd);
            assert(twelve.ReservedBytes <= twelve.TotalBytes,
                $"spool budget: 20 workers on 12-minute tracks in 8 GiB stay inside the budget ({twelve.ReservedBytes:N0} of {twelve.TotalBytes:N0})");
            var mixed = new SpoolBudget(4 * GiB, workers: 16, typicalSeconds: 250);
            mixed.Reserve(5500, Cd);
            for (int i = 0; i < 15; i++) mixed.Reserve(240 + i, Cd);
            assert(mixed.ReservedBytes <= mixed.TotalBytes,
                $"spool budget: one 5,500-second track then fifteen ordinary ones on 16 workers in 4 GiB stay inside the budget ({mixed.ReservedBytes:N0} of {mixed.TotalBytes:N0})");
            var alone = new SpoolBudget(4 * GiB, workers: 16, typicalSeconds: 250);
            var aloneGrant = alone.Reserve(240, Cd);
            assert(aloneGrant.SpoolBytes > 0, "spool budget: room left for the other workers does not stop an ordinary track being held");
        }

        static void Order(Action<bool, string> assert)
        {
            // 40 four-minute tracks with an album of six one-hour mixes in the middle, and one three-hour track at the very end.
            var secs = new List<double>();
            for (int i = 0; i < 20; i++) secs.Add(240);
            for (int i = 0; i < 6; i++) secs.Add(3600);
            for (int i = 0; i < 20; i++) secs.Add(240);
            secs.Add(3 * 3600);
            var idx = Enumerable.Range(0, secs.Count).ToList();
            int threeHour = secs.Count - 1, firstMix = 20;
            long smallBudget = new SpoolBudget(6 * GiB).TotalBytes, bigBudget = new SpoolBudget(120 * GiB).TotalBytes;

            // A small machine: 8 workers sharing 3.6 GiB. An hour of audio needs more than an even split of that.
            var q = new LongFirstQueue<int>(idx, i => secs[i], 8, smallBudget);
            assert(q.LongCount == 7 && q.IsLong(threeHour) && q.IsLong(firstMix) && !q.IsLong(0),
                $"scan order: the long set is the tracks that need more than an even split of the budget ({q.LongCount} of {q.Count})");
            assert(q.Lanes == 1, $"scan order: only as many long tracks run at once as the budget holds beside the ordinary ones ({q.Lanes} on 3.6 GiB)");
            assert(q.TryTake(out int a, out bool aLong) && a == threeHour && aLong, $"scan order: the longest track is taken first, wherever the library has it (took {a})");
            assert(q.TryTake(out int b2, out bool bLong) && !bLong && b2 == 0,
                $"scan order: with the long lane busy, the next worker takes the first ordinary track in library order (took {b2})");
            q.Done(true);
            assert(q.TryTake(out int c, out bool cLong) && cLong && c == firstMix, $"scan order: when the long lane frees up it takes the next longest (took {c})");

            // Drained the way 8 workers would: the long lane holds its track while the others turn over.
            var seen = new List<int> { a, b2, c };
            int longBusy = 1, worst = 1;
            bool ordinaryTookLong = false;
            while (q.TryTake(out int t, out bool tl))
            {
                seen.Add(t);
                if (tl) { longBusy++; worst = Math.Max(worst, longBusy); }
                else if (q.IsLong(t)) ordinaryTookLong = true;
                if (seen.Count % 12 == 0 && longBusy > 0) { q.Done(true); longBusy--; }
            }
            assert(seen.Count == secs.Count && seen.Distinct().Count() == secs.Count, $"scan order: every track is taken exactly once ({seen.Distinct().Count()} of {secs.Count})");
            assert(!ordinaryTookLong, "scan order: an ordinary worker never takes a long-set track — an album of long mixes is not taken all at once");

            // The cap holds for as long as there is other work; after that a free worker takes a long track rather than wait.
            var q3 = new LongFirstQueue<int>(idx, i => secs[i], 8, smallBudget);
            int held = 0, ordinaryTaken = 0;
            while (ordinaryTaken < 40 && q3.TryTake(out _, out bool l3)) { if (l3) held++; else ordinaryTaken++; }
            assert(held == 1 && ordinaryTaken == 40, $"scan order: one long track runs at a time while ordinary tracks remain (long taken {held}, ordinary {ordinaryTaken})");
            assert(q3.TryTake(out int t4, out bool l4) && l4 && q3.IsLong(t4), "scan order: when the ordinary tracks run out, a free worker takes a long one rather than wait");

            // The long set runs out first: the long lane goes on taking the longest of what is left.
            var q4 = new LongFirstQueue<int>(idx, i => secs[i], 8, smallBudget);
            for (int k = 0; k < 7; k++) { q4.TryTake(out _, out _); q4.Done(true); }
            assert(q4.TryTake(out int t6, out bool l6) && l6 && !q4.IsLong(t6) && t6 == 0, $"scan order: with the long set done, the long lane takes ordinary tracks (took {t6})");

            // A large machine: every track fits an even split, so none is held back — and the longest still starts first.
            var big = new LongFirstQueue<int>(idx, i => secs[i], 8, bigBudget);
            assert(big.LongCount == 0 && big.Lanes == 1, $"scan order: with room for every track, none is set aside ({big.LongCount} long, {big.Lanes} lane)");
            assert(big.TryTake(out int g1, out bool g1l) && g1 == threeHour && g1l && big.TryTake(out int g2, out _) && g2 == 0,
                "scan order: and the longest track still starts first, so it does not end the run on its own");
            var bigRest = big.Feed().Select(p => p.Key).ToList();
            assert(bigRest.SequenceEqual(idx.Where(i => i != threeHour && i != 0)), "scan order: the rest is library order, the hour mixes together");

            // A bigger budget holds more long tracks at once.
            var mid = new LongFirstQueue<int>(idx, i => secs[i], 8, new SpoolBudget(16 * GiB).TotalBytes);
            assert(mid.LongCount == 7 && mid.Lanes > 1 && mid.Lanes <= 8, $"scan order: more free RAM, more long tracks at once ({mid.Lanes} lanes on 9.6 GiB)");

            // Nothing to order by, or nothing to stagger against: library order, exactly.
            var flat = new LongFirstQueue<int>(idx, _ => 0, 8, smallBudget);
            assert(flat.LongCount == 0 && flat.Feed().Select(p => p.Key).SequenceEqual(idx), "scan order: a library with no lengths is taken in library order");
            var one = new LongFirstQueue<int>(idx, i => secs[i], 1, smallBudget);
            assert(one.LongCount == 0 && one.Lanes == 0 && one.Feed().Select(p => p.Key).SequenceEqual(idx), "scan order: one worker takes the library in order");
            var noRam = new LongFirstQueue<int>(idx, i => secs[i], 8, 0);
            assert(noRam.LongCount == 0 && noRam.TryTake(out int n1, out _) && n1 == threeHour, "scan order: with no reading of free RAM nothing is set aside, and the longest still starts first");
            var empty = new LongFirstQueue<int>(new List<int>(), _ => 1, 8, smallBudget);
            assert(!empty.TryTake(out _, out _), "scan order: an empty list hands out nothing");
            var bad = new LongFirstQueue<int>(new List<int> { 0, 1, 2 }, i => i == 1 ? double.NaN : i == 2 ? -5 : 100, 4, smallBudget);
            assert(bad.Feed().Select(p => p.Key).OrderBy(x => x).SequenceEqual(new[] { 0, 1, 2 }), "scan order: a length that is not a number or is negative is treated as unknown, and the track is still scanned");
        }
    }
}
