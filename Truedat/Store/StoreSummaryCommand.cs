using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Truedat.Store
{
    /// <summary>One track as the summary needs it: the whole-track key and its votes, the two edge windows' keys, and
    /// the three chroma vectors (already unit-scaled, so a distance is one dot product).</summary>
    internal sealed class SummaryTrack
    {
        public string? WholeCode;                         // Camelot from the FLAT key/mode — what the hub acts on
        public string? HeadCode, TailCode;                // the edge rows' own codes (EDMA, see EdgeKey.Vote)
        public int FlatAgree, FlatPresent;                // votes agreeing with the flat pair (the hub's count)
        public int EdmaAgree, EdmaPresent;                // votes agreeing with the EDMA vote (equal terms with an edge)
        public int HeadAgree, HeadPresent, TailAgree, TailPresent;
        public string Provenance = "missing";             // which rung supplied the flat pair
        public double[]? W12, H36, T36;                   // whole-track hpcp12, head window HPCP36, tail window HPCP36
    }

    /// <summary>Read-only summary of a truedat store, short enough to paste into a forum post
    /// (<c>--store-summary</c>). Six questions, in the order they inform a decision:
    ///
    /// <list type="number">
    /// <item>how often the three key profiles agree, per region, and which rung supplied the whole-track key;</item>
    /// <item>how the tracks spread over the Camelot codes;</item>
    /// <item>how often the start, whole and end keys agree with each other under the hub's compatibility rule;</item>
    /// <item>how close whole-track chroma puts two tracks, and where a same-key pair falls among all pairs;</item>
    /// <item>whether the edge key or the whole-track key better predicts the chroma actually present at a join;</item>
    /// <item>how this library's raw feature spread sits against the range the shipped mood model was fitted on, at
    /// both ends, with the statistics needed to pool several libraries.</item>
    /// </list>
    ///
    /// <para>The sections that read whole-track values use every track. The ones that need an edge window say how
    /// many tracks that is, so a store whose edges are complete, partly filled or absent prints a block that reads
    /// the same way.</para>
    ///
    /// <para>It opens the store through <see cref="SqliteDb.OpenReadOnly"/>, so whatever it does it cannot change a
    /// byte of the database (proven by hashing one either side of a run); opening a WAL store still creates the
    /// <c>-shm</c> sidecar every reader needs, which is not a change to the store. It prints no path, host name or
    /// track name INSIDE the block it tells the user to copy: the whole point is that the output can be pasted in
    /// public. Anything it cannot compute it names as not computed rather than printing a zero.</para></summary>
    internal static class StoreSummaryCommand
    {
        /// <summary>Ordered pairs sampled for the join comparison. 200k is well inside a second over pre-unit-scaled
        /// vectors and makes the third decimal of a separation figure stable across runs.</summary>
        internal const int PairSamples = 200000;

        /// <summary>A fixed seed, so two people posting the same store post the same numbers and a difference between
        /// two posts is a difference between two libraries.</summary>
        internal const int Seed = 20261110;

        internal sealed class Feat
        {
            public readonly string Name; public readonly double Mean, Std;
            public Feat(string name, double mean, double std) { Name = name; Mean = mean; Std = std; }
        }

        /// <summary>The fitted scale of the shipped mood model's served head — its own statement of the range it was
        /// trained against, which is the reference part 4 compares a library to. Copied from
        /// <c>MBXH/Core/Resources/mood-model.json</c> <c>scaler.mean</c> / <c>scaler.std</c> and tagged with that
        /// model's version, because the file is EMBEDDED in mb_MBXHub.dll and a user's box has no loose copy to read.
        /// A refit on the hub side moves these numbers; <see cref="ModelTag"/> is what makes that visible rather than
        /// silent, and an override file beside the hub's data wins when one exists.
        ///
        /// <para>Generated from the model, never typed: see docs/plans/2026-11-10-store-summary-script-plan.md.</para></summary>
        internal const string ModelTag = "the hub's default mood model, v3, trained 2026-10-21";

        internal static readonly Feat[] ModelScale =
        {
            new Feat("spectralCentroid", 1349.6592695881102, 489.4554968998788),
            new Feat("spectralFlux", 0.07289093431618528, 0.018938641092391063),
            new Feat("spectralFlatness", 0.0, 1.0),
            new Feat("danceability", 1.1333183569936944, 0.18946539921640737),
            new Feat("dissonance", 0.4526261249772268, 0.02631216938391245),
            new Feat("pitchSalience", 0.5430618874111857, 0.05280757557362471),
            new Feat("chordsChangesRate", 0.06411937161035258, 0.026556377729223757),
            new Feat("spectralSkewness", 1.9050803957565543, 0.8576058446086032),
            new Feat("spectralEntropy", 7.299587929705494, 0.4981125591184689),
            new Feat("spectralComplexity", 16.72171910254056, 6.669273500561508),
            new Feat("hpcpCrest", 14.039764704233638, 2.077187753013806),
            new Feat("hpcpEntropy", 1.9515971537480306, 0.2909574997562452),
            new Feat("hfc", 20.18034250318821, 13.641659240685518),
            new Feat("beatsLoudness", 0.03764676205557998, 0.020040125494239322),
            new Feat("chordsStrength", 0.5206294799388985, 0.041091182942454306),
            new Feat("bpm", 124.4045671763093, 25.072797714203887),
            new Feat("loudness", -13.709401039842051, 4.114130265217799),
            new Feat("onsetRate", 3.4222484129097333, 0.9167083216378258),
            new Feat("zeroCrossingRate", 0.06180719389828691, 0.02459803498939629),
            new Feat("spectralRms", 0.004402936123996226, 0.0009933132882444988),
            new Feat("dynamicRange", 6.653648415712528, 3.903491636708428),
        };

        /// <summary>A feature whose fitted scale is degenerate carries no reference to compare against: with mean 0 and
        /// std 1 the "sigma" of a library's value is just the value. Named so the output can say which and why rather
        /// than print a column of nonsense. (spectralFlatness, mood-model v3 — reported to the hub lane 2026-11-10.)</summary>
        internal static bool HasUsableScale(Feat f) => f.Std > 0 && !(f.Mean == 0.0 && f.Std == 1.0);

        /// <summary>Where the same model's valence side stops reading an input: each standardized input is clipped to
        /// plus or minus this many fitted standard deviations before it is combined (<c>zBound</c> on the served
        /// head's valence axis in the same <c>mood-model.json</c>; <c>MoodModelLoader</c> applies it). Its arousal
        /// axis carries no bound. The range table counts the tracks past it, because that is the line beyond which
        /// one side of the model stops telling tracks apart and the other extrapolates. Tagged by
        /// <see cref="ModelTag"/> like the scale: a refit can move it.</summary>
        internal const double ValenceInputBound = 4.0;

        internal static int Run(string storePath, bool explanations = true)
        {
            Explanations = explanations;
            if (!File.Exists(storePath))
            {
                // The usual case for a reader: the store is off by default, so this library simply has none yet. The
                // resolver has already GUESSED a location by then, so this branch — not the "storePath == null" one —
                // is where a user without a store actually lands, and it is where the remedy has to be.
                Console.Error.WriteLine($"Error: no store at {storePath}");
                Console.Error.WriteLine("  This library has no store yet. Seed one from the existing catalog with");
                Console.Error.WriteLine("  truedat --store-import, then run a scan with --store --edges to fold the");
                Console.Error.WriteLine("  edge windows this summary reports on. Name a different one with --store <path>.");
                return 1;
            }
            // A WAL store with no shared-memory file cannot be read by a read-only connection at all, because
            // recovering the WAL would be a write. Say that, with the remedy, rather than opening read-write.
            SqliteDb db;
            try { db = SqliteDb.OpenReadOnly(storePath); }
            catch (SqliteException ex)
            {
                Console.Error.WriteLine("Error: the store could not be opened read-only.");
                Console.Error.WriteLine("  " + ex.Message);
                Console.Error.WriteLine("  If a -wal file sits beside it, run this when nothing else holds the store, or after a truedat run has closed it.");
                return 1;
            }

            using (db)
            {
                var lines = new List<string>();
                try { Build(db, lines); }
                catch (SqliteException ex)
                {
                    // One sentence, and the remedy that matches the actual failure: a locked store and a store this
                    // report cannot read are different problems and used to print the same (wrong) hint.
                    Console.Error.WriteLine("Error: the store could not be read.");
                    Console.Error.WriteLine("  " + FirstLine(ex.Message));
                    if (ex.ResultCode == 5 || ex.ResultCode == 6)
                        Console.Error.WriteLine("  Something else is holding it; run this when no scan or hub is writing.");
                    else
                        Console.Error.WriteLine("  Run truedat once with the store on, which migrates it on open (taking a backup first), then try again.");
                    return 1;
                }
                // The store's path is already on the console from the resolver; it must never reach the block, which
                // is the part that gets pasted somewhere public.
                Console.WriteLine();
                Console.WriteLine("--- copy from here -------------------------------------------------------");
                foreach (var l in lines) Console.WriteLine(l);
                Console.WriteLine("--- to here --------------------------------------------------------------");
                Console.WriteLine();
                Console.WriteLine("Nothing between those markers names a path, a machine or a track; it is safe to post.");
                return 0;
            }
        }

        static void Build(SqliteDb db, List<string> o)
        {
            int schema = SchemaMigrator.ReadVersion(db);   // the schema_version table, not PRAGMA user_version (which truedat never sets)

            // What this report can read depends on the schema, and it CANNOT migrate: a migration is a write, and this
            // connection is read-only on purpose. So name the shortfall and the remedy rather than letting a query
            // fail with "no such table" and a page of SQL.
            //   v2  track_content / track_copy (the per-media split) — keys and the feature range
            //   v4  the typed edge table with its per-window key and hpcp — everything about edges
            if (schema < 2)
            {
                o.Add("truedat store summary  " + VersionInfo.Display);
                o.Add($"schema v{schema}");
                Note(o, "not computed: this store predates the per-media split, so it holds none of the columns this " +
                        "report reads. Run truedat once with the store on and it migrates on open (it takes a backup " +
                        "first), then run this again.", "");
                return;
            }
            var edgeVersion = Meta(db, "edges.selectedVersion");
            long contentRows = Count(db, "SELECT COUNT(*) FROM track_content");

            // The edges line must describe the ROWS, not just the store_meta key: that key survives after the rows are
            // gone, and after a producer bump it names a version this store may hold nothing at. Both cases print as
            // "edges@v3" otherwise, which reads as coverage the store does not have.
            bool hasEdgeTable = schema >= 4;
            long edgeAtVersion = hasEdgeTable ? Count(db, "SELECT COUNT(*) FROM edge WHERE producer_version = (SELECT value FROM store_meta WHERE key = 'edges.selectedVersion')") : 0;
            long edgeAnyVersion = hasEdgeTable ? Count(db, "SELECT COUNT(*) FROM edge") : 0;
            string edges =
                !hasEdgeTable ? $"not readable at schema v{schema} (the typed edge table arrived at v4)" :
                string.IsNullOrEmpty(edgeVersion) ? "none selected" :
                edgeAnyVersion == 0 ? edgeVersion + " selected, but the store holds no edge rows" :
                edgeAtVersion == 0 ? edgeVersion + $" selected, which this store holds none of ({edgeAnyVersion} rows at other versions — a scan with --edges refolds them)" :
                edgeVersion;

            // The version token fits beside the counts; any of the shortfall descriptions above is a sentence and gets
            // its own wrapped line. Inline, one of them ran the header to 90 characters — past the block width, which
            // exists so that several posted blocks line up beside each other.
            bool edgesInline = edges.IndexOf(' ') < 0;
            o.Add("truedat store summary  " + VersionInfo.Display);
            o.Add($"schema v{schema}   tracks {contentRows}" + (edgesInline ? $"   edges {edges}" : ""));
            if (!edgesInline) Note(o, "edges: " + edges, "");

            var tracks = LoadTracks(db, edgeVersion, hasEdgeTable, out int wholeKeyRows, out int edgeRows, out int chromaRows);
            o.Add($"whole key {wholeKeyRows}   both window KEYS {edgeRows}   all three CHROMA vectors {chromaRows}");
            if (!hasEdgeTable)
                Note(o, "Everything about edges below is 'not computed': this store is at schema v" + schema +
                        " and the typed edge table arrived at v4. Run truedat once with the store on to migrate it " +
                        "on open (it takes a backup first), then run this again.", "");
            // Those two differ on purpose: a window always folds a chroma vector, but its key is EDMA's answer and
            // EDMA declines a window it cannot decide (EdgeKey.Vote, strength under 0.4). The gap is that decline rate.
            int chromaNoKey = tracks.Count(t => t.H36 != null && t.T36 != null && (t.HeadCode == null || t.TailCode == null));
            if (chromaNoKey > 0)
                o.Add(CoverageGapLine(chromaNoKey));
            o.Add("");

            // Whole-track sections read every track; the ones that need an edge window say how many tracks that is.
            // So a store whose edges are complete, partly filled or absent all print a block that reads the same way.
            KeyAgreement(tracks, wholeKeyRows, o);
            KeyShares(tracks, o);
            RegionalCompatibility(tracks, o);
            WholeTrackSound(tracks, o);
            JoinSeparation(tracks, o);
            FeatureSpread(db, contentRows, o);

            Explain(o, "Limits: chroma distance is shared notes, not what a listener hears. The edge key is measured " +
                    "from the same audio as the yardstick, so its lead is an upper bound. Pairs are random, not a " +
                    "picker's own joins.", "");
        }

        /// <summary>The decline rate, as a row rather than a sentence: it sits beside the coverage counts it qualifies,
        /// and it is the same line in both modes, so <c>--store-summary-min</c> stays a strict subset of the full
        /// block. A window always folds a chroma vector, but its key is EDMA's answer and EDMA declines a window it
        /// cannot decide (EdgeKey.Vote, strength under 0.4) — this is that gap.</summary>
        internal static string CoverageGapLine(int tracks) =>
            $"window chroma, no decidable key in one or both windows: {tracks}";

        // ---------------------------------------------------------------- part 1

        internal static void KeyAgreement(List<SummaryTrack> tracks, int wholeKeyRows, List<string> o)
        {
            o.Add("KEYS — how often the three profiles agree");
            if (wholeKeyRows == 0) { o.Add("  not computed: no track carries a whole-track key"); o.Add(""); return; }

            // Two counts of the same thing against two references, because they answer different questions and on a
            // fallback row they differ: the flat pair is what the hub's trust term acts on, the EDMA vote is what an
            // edge row reports, so only the second compares with the edge columns on equal terms.
            o.Add("  whole, vs the stored key: " + Buckets(tracks.Select(t => (t.FlatAgree, t.FlatPresent))));
            o.Add("  whole, vs the EDMA vote:  " + Buckets(tracks.Select(t => (t.EdmaAgree, t.EdmaPresent))));

            var head = tracks.Where(t => t.HeadPresent > 0).Select(t => (t.HeadAgree, t.HeadPresent)).ToList();
            var tail = tracks.Where(t => t.TailPresent > 0).Select(t => (t.TailAgree, t.TailPresent)).ToList();
            o.Add("  start window:             " + (head.Count > 0 ? Buckets(head) : "not computed: no start-window key"));
            o.Add("  end window:               " + (tail.Count > 0 ? Buckets(tail) : "not computed: no end-window key"));
            Explain(o, "Agreement decides how much say a key gets in AutoQ: full at 3 of 3, half at 2 of 3, none below. Row 1 counts against the key AutoQ reads for a whole track; rows 2-4 count against EDMA, so only those three compare with each other. With edge transitions on it is the two window keys that meet at a join.");

            // The asymmetry that makes the two whole-track rows comparable only once it is stated. The two rows are
            // IDENTICAL whenever every row's key came off the EDMA rung, so claim a difference only when there is one.
            var prov = tracks.GroupBy(t => t.Provenance).OrderByDescending(g => g.Count()).ToList();
            int fallback = tracks.Count(t => t.Provenance != "edma" && t.Provenance != "missing");
            Explain(o, "An edge key IS EDMA's answer, so its agreement is never 0 of n. A whole-track key can fall back to "
                    + (fallback > 0
                        ? $"another estimator, and {fallback} here did — on those rows 0 of 3 is possible, which is why the two rows above differ."
                        : "another estimator, and then 0 of 3 is possible — no row here did, so the two rows above are the same."));
            o.Add("  the stored key came from: " + string.Join(", ", prov.Select(g => $"{g.Key} {g.Count()}")));
            o.Add("");
        }

        static string Buckets(IEnumerable<(int Agree, int Present)> rows)
        {
            var l = rows.ToList();
            if (l.Count == 0) return "not computed: no rows";
            int n = l.Count;
            int three = l.Count(x => x.Present == 3 && x.Agree == 3);
            int two = l.Count(x => x.Present == 3 && x.Agree == 2);
            int one = l.Count(x => x.Present == 3 && x.Agree == 1);
            int zero = l.Count(x => x.Present == 3 && x.Agree == 0);
            int partial = l.Count(x => x.Present != 3);
            var s = $"3 of 3 {Pct(three, n)}  2 of 3 {Pct(two, n)}  1 of 3 {Pct(one, n)}";
            if (zero > 0) s += $"  0 of 3 {Pct(zero, n)}";
            if (partial > 0) s += $"  fewer than 3 carried {Pct(partial, n)}";
            return s + $"  (n={n})";
        }

        // ---------------------------------------------------------------- part 2

        internal static void RegionalCompatibility(List<SummaryTrack> tracks, List<string> o)
        {
            o.Add("KEYS — start, whole and end against each other (hub compatibility rule)");
            var complete = tracks.Where(t => t.WholeCode != null && t.HeadCode != null && t.TailCode != null).ToList();
            if (complete.Count == 0) { o.Add("  not computed: no track carries all three keys"); o.Add(""); return; }

            int same = complete.Count(t => t.HeadCode == t.WholeCode && t.WholeCode == t.TailCode);
            o.Add($"  all three the same Camelot code: {Pct(same, complete.Count)}  (n={complete.Count})");

            Pair(o, "start vs whole", complete.Select(t => StoreSummary.CamelotDistance(t.HeadCode, t.WholeCode)));
            Pair(o, "whole vs end  ", complete.Select(t => StoreSummary.CamelotDistance(t.WholeCode, t.TailCode)));
            Pair(o, "start vs end  ", complete.Select(t => StoreSummary.CamelotDistance(t.HeadCode, t.TailCode)));

            var worst = complete.Select(t => Math.Max(StoreSummary.CamelotDistance(t.HeadCode, t.WholeCode),
                                            Math.Max(StoreSummary.CamelotDistance(t.WholeCode, t.TailCode),
                                                     StoreSummary.CamelotDistance(t.HeadCode, t.TailCode))));
            Pair(o, "worst of three", worst);
            o.Add("");
        }

        static void Pair(List<string> o, string label, IEnumerable<double> distances)
        {
            var l = distances.ToList();
            o.Add($"  {label}: compatible {Pct(l.Count(d => d == 0.0), l.Count)}  near {Pct(l.Count(d => d == 0.5), l.Count)}  clash {Pct(l.Count(d => d == 1.0), l.Count)}");
        }

        // ---------------------------------------------------------------- key shares

        /// <summary>How the library's tracks spread over the 24 Camelot codes, as shares so two libraries of
        /// different sizes read side by side. A rule that joins by key can only pick from the codes compatible with
        /// the current one, so a thin code is a thin band of candidates — which a library-wide agreement figure
        /// cannot show. Whole-track keys only: it needs no edge window.</summary>
        internal static void KeyShares(List<SummaryTrack> tracks, List<string> o)
        {
            o.Add("KEYS — share of tracks on each Camelot code (the stored key), percent");
            var counts = new int[2, 13];
            int n = 0;
            foreach (var t in tracks)
            {
                if (!StoreSummary.TryParseCode(t.WholeCode, out int hour, out char ring)) continue;
                counts[ring == 'A' ? 0 : 1, hour]++;
                n++;
            }
            if (n == 0) { o.Add("  not computed: no track carries a whole-track key"); o.Add(""); return; }

            var hours = Enumerable.Range(1, 12).ToList();
            o.Add("    " + string.Concat(hours.Select(h => h.ToString(CultureInfo.InvariantCulture).PadLeft(6))));
            for (int r = 0; r < 2; r++)
            {
                int ring = r;
                o.Add("  " + (ring == 0 ? "A" : "B") + " " +
                      string.Concat(hours.Select(h => (100.0 * counts[ring, h] / n).ToString("F1", CultureInfo.InvariantCulture).PadLeft(6))));
            }
            int minor = hours.Sum(h => counts[0, h]);
            o.Add($"  minor (A) {Pct(minor, n)}   major (B) {Pct(n - minor, n)}   (n={n})");
            Explain(o, "A is the minor ring and B the major. A join by key draws its candidates from one code, the same " +
                    "hour on the other ring and the two neighboring hours on its own, so a thin code leaves few to " +
                    "choose from.");
            o.Add("");
        }

        // ---------------------------------------------------------------- whole-track sound

        /// <summary>Whole-track chroma between tracks, from every track that carries a key and a chroma vector — edge
        /// windows or not. It used to be read off the join section's pairs, which only exist for tracks a scan has
        /// folded AND whose two window keys EDMA decided; on a library still filling its edges that was a small and
        /// unrepresentative part, for a measurement that never needed an edge.
        ///
        /// <para>The self-check is a RELATIONSHIP, not a figure. An earlier wording quoted the two means measured on
        /// two libraries as what a healthy store reads; a healthy library need not sit on them, and the chroma
        /// grouping fix moves them again. What must hold everywhere is that pairs sharing a key sit clearly closer
        /// than pairs that do not.</para>
        ///
        /// <para>The last row is the hub's own rank (<see cref="StoreSummary.CloserThanPercent"/>) applied to the
        /// mean same-key pair: what a join by key amounts to on this library's scale of pair distances.</para></summary>
        internal static void WholeTrackSound(List<SummaryTrack> tracks, List<string> o)
        {
            o.Add("SOUND — whole-track chroma between tracks (needs no edge windows)");
            var usable = tracks.Where(t => t.W12 != null && t.WholeCode != null).ToList();
            if (usable.Count < 2)
            {
                Note(o, "not computed: fewer than two tracks carry both a whole-track key and whole-track chroma (hpcp12).");
                o.Add("");
                return;
            }

            var rnd = new Random(Seed);
            var all = new List<double>(PairSamples);
            var same = new List<double>(); var other = new List<double>();
            for (int i = 0; i < PairSamples; i++)
            {
                int ia = rnd.Next(usable.Count), ib = rnd.Next(usable.Count);
                if (ia == ib) continue;
                var a = usable[ia]; var b = usable[ib];
                double d = StoreSummary.CosineDistance(a.W12!, b.W12!);
                all.Add(d);
                (a.WholeCode == b.WholeCode ? same : other).Add(d);
            }
            double sameMean = StoreSummary.Mean(same);
            o.Add($"  pairs sharing a key:   {F(sameMean, 4)}  (n={same.Count})");
            o.Add($"  pairs on another key:  {F(StoreSummary.Mean(other), 4)}  (n={other.Count})");
            all.Sort();
            o.Add($"  any pair: p5 {F(StoreSummary.Percentile(all, 5), 3)}  p10 {F(StoreSummary.Percentile(all, 10), 3)}  " +
                  $"p25 {F(StoreSummary.Percentile(all, 25), 3)}  p50 {F(StoreSummary.Percentile(all, 50), 3)}  (n={all.Count})");

            double closeness = usable.Count >= StoreSummary.MinClosenessTracks && same.Count > 0
                ? StoreSummary.CloserThanPercent(sameMean, all) : double.NaN;
            if (double.IsNaN(closeness))
                Note(o, "closeness of a same-key pair: not computed. It needs " + StoreSummary.MinClosenessTracks +
                        " tracks with a key and whole-track chroma, and at least one sampled pair sharing a key.");
            else
                o.Add($"  pairs sharing a key are closer than {F(closeness, 1)}% of this library's pairs");

            Explain(o, "The first two rows are a self-check. Pairs sharing a key must sit clearly closer than pairs on " +
                    "another key; if they do not, this store or this reader is wrong and the rest of this block should " +
                    "not be trusted. The last row puts a join by key on this library's own scale: the share of its " +
                    "pairs that an average same-key pair is closer than. These rows read whole-track chroma only, so " +
                    "they cover every track whether or not its edge windows have been folded.");
            o.Add("");
        }

        // ---------------------------------------------------------------- part 3

        internal static void JoinSeparation(List<SummaryTrack> tracks, List<string> o)
        {
            // Not headed "EDGES": the rows below compare key verdicts AND a whole-track rule that needs no edge
            // capture at all, so naming the section after edges invited reading the last row as a verdict on them.
            o.Add("AT A JOIN — which key verdict tracks the audio, and how sound compares");
            var usable = tracks.Where(t => t.W12 != null && t.H36 != null && t.T36 != null
                                        && t.WholeCode != null && t.HeadCode != null && t.TailCode != null).ToList();
            if (usable.Count < 2)
            {
                Note(o, "not computed: fewer than two tracks carry a whole key, both window keys and all three chroma " +
                        "vectors. Window chroma needs a scan with --edges; the whole-track vector needs hpcp12.");
                o.Add("");
                return;
            }

            // How much of the library everything below describes. A store whose edge windows are still being filled
            // (an interrupted --refresh, or a background fill) answers this section from the part folded so far, and
            // without this row a block from 1% of a library reads exactly like a block from all of it.
            o.Add($"  tracks carrying everything a join needs: {usable.Count} of {tracks.Count} ({Pct(usable.Count, tracks.Count)})");
            Explain(o, "The rows below describe only those tracks: a whole key, a decided key in both windows and all " +
                    "three chroma vectors. While a library's edge windows are still being filled that is a part of " +
                    "it, and not a random sample.");

            var rnd = new Random(Seed);
            var joinByEdge = Classes(); var joinByWhole = Classes();
            // The same two splits over only the pairs the HUB would have an opinion on. A key the scorer does not
            // trust has no say: PfsOrderer.TrustedKeyDistance returns null when EITHER side's HarmonicTrust is 0, so a
            // 1-of-3 or 1-of-2 key reads as "no opinion", not as compatible or a clash. Counting every key — as the
            // rows above do — therefore measures something the hub never acts on, which is why both are printed.
            var trustedByEdge = Classes(); var trustedByWhole = Classes();
            var join = new List<double>(PairSamples);
            // The whole-track distance of the same pairs, for the one reading that needs both: how much of what
            // happens at a join is already visible in the whole-track vector.
            var wholeChroma = new List<double>(PairSamples);

            for (int i = 0; i < PairSamples; i++)
            {
                int ia = rnd.Next(usable.Count), ib = rnd.Next(usable.Count);
                if (ia == ib) continue;
                var a = usable[ia]; var b = usable[ib];
                double d = StoreSummary.CosineDistance(a.T36!, b.H36!);     // A's end against B's start: the join
                join.Add(d);
                double de = StoreSummary.CamelotDistance(a.TailCode, b.HeadCode);
                double dw = StoreSummary.CamelotDistance(a.WholeCode, b.WholeCode);
                joinByEdge[de].Add(d);
                joinByWhole[dw].Add(d);
                wholeChroma.Add(StoreSummary.CosineDistance(a.W12!, b.W12!));
                if (Trusted(a.TailAgree, a.TailPresent) && Trusted(b.HeadAgree, b.HeadPresent)) trustedByEdge[de].Add(d);
                if (Trusted(a.FlatAgree, a.FlatPresent) && Trusted(b.FlatAgree, b.FlatPresent)) trustedByWhole[dw].Add(d);
            }

            o.Add($"  join chroma distance over {join.Count} random ordered pairs: mean {F(StoreSummary.Mean(join), 4)}");
            o.Add("  mean join distance compatible        near            clash           sep");
            Verdict(o, "edge keys", joinByEdge);
            Verdict(o, "whole keys", joinByWhole);
            Verdict(o, "edge trusted", trustedByEdge);
            Verdict(o, "whole trusted", trustedByWhole);
            Explain(o, "sep 0.50 = the verdict says nothing about the chroma at the join; 1.00 = perfect. The first " +
                    "two rows count every key; the 'trusted' rows keep only pairs where both keys carry trust above " +
                    "zero, which is the only case the hub acts on at all.");

            // Two RULES compared on one yardstick: pick the next track by key, or by the whole-track chroma vector.
            // It says nothing about what edge capture is worth, and the earlier framing of it as the number the
            // edges decision turns on was wrong (operator, 2026-11-10, relayed by restfulbee:architect): a join also
            // carries tempo, brightness, tension and loudness, and none of those is in this row. The question it
            // does answer is how joining by sound compares with joining by key — better, different, or worse.
            GapClosed(usable, rnd, o);
            Explain(o, "That row is the share of the gap between a random next track and the best one available that " +
                    "each rule closes: 0.00 is no better than random, 1.00 always picks the best. It compares two " +
                    "ways of choosing a next track; it is not a verdict on edge capture, which also carries tempo, " +
                    "brightness, tension and loudness that this row does not look at.");
            o.Add("");

            ChromaReadings(usable, join, wholeChroma, o);
        }

        /// <summary>The two chroma readings that make the join numbers above readable, and no others.
        ///
        /// <para>A scale: a track's own start window against its own end window. Without it nobody can tell whether
        /// "compatible 0.24 against clash 0.34" is a wide gap or a rounding error, because the unit is arbitrary.</para>
        ///
        /// <para>A reason: how well whole-track chroma distance tracks the distance actually at the join. It is the
        /// answer to "why does a whole-track vector beat a window key".</para>
        ///
        /// <para>The whole-track self-check lived here once. It needs no edge window, so it moved to
        /// <see cref="WholeTrackSound"/>, where it reads every track and not only the folded ones.</para>
        ///
        /// <para>Deliberately NOT here: head-against-head or tail-against-tail (no join is ever made of those, so the
        /// number informs nothing), window-against-whole-track for the same track (an edge-quality question, not one
        /// of the two decisions this block feeds, and already measured elsewhere), and anything per chroma bin (no
        /// consumer reads a bin).</para></summary>
        static void ChromaReadings(List<SummaryTrack> usable, List<double> join, List<double> wholeChroma, List<string> o)
        {
            o.Add("CHROMA — a scale for the join distances");
            var own = usable.Select(t => StoreSummary.CosineDistance(t.H36!, t.T36!)).ToList();
            o.Add($"  a track's own start vs its own end:  {F(StoreSummary.Mean(own), 4)}  (n={own.Count})");
            o.Add($"  whole-track distance vs join distance: r = {F(StoreSummary.Pearson(wholeChroma, join), 2)}");
            Explain(o, "The first line is the yardstick: two ends of one track are this far apart, so a join closer than " +
                    "that is as close as music gets to itself. The second says how much of what happens at a join is " +
                    "already visible in the whole-track vector, which needs no edge capture.");
            o.Add("");
        }

        static void Verdict(List<string> o, string label, Dictionary<double, List<double>> byClass)
        {
            var c = byClass[0.0]; var n = byClass[0.5]; var x = byClass[1.0];
            var auc = StoreSummary.Auc(c, x);
            o.Add($"  {label,-14} {Cell(c),-17} {Cell(n),-15} {Cell(x),-15}" +
                  (double.IsNaN(auc) ? "not computed" : "   " + F(auc, 3)));
        }

        static string Cell(List<double> xs) => $"{F(StoreSummary.Mean(xs), 4)} ({xs.Count})";

        static Dictionary<double, List<double>> Classes() =>
            new Dictionary<double, List<double>> { [0.0] = new List<double>(), [0.5] = new List<double>(), [1.0] = new List<double>() };

        /// <summary>Whether the hub would let this key have a say at all: PfsOrderer.TrustedKeyDistance drops the
        /// comparison when HarmonicTrust is 0 on either side.</summary>
        static bool Trusted(int agreeing, int present) => StoreSummary.HarmonicTrust(agreeing, present) > 0;

        /// <summary>How much of the gap between a random next track and the best available next track — measured on the
        /// join's own window chroma — each rule closes. 0 = no better than random, 1 = always picks the best.</summary>
        static void GapClosed(List<SummaryTrack> usable, Random rnd, List<string> o)
        {
            const int Trials = 4000, Pool = 8;
            double randomSum = 0, bestSum = 0, keySum = 0, edgeSum = 0, chromaSum = 0; int n = 0;
            for (int t = 0; t < Trials; t++)
            {
                var from = usable[rnd.Next(usable.Count)];
                var pool = new List<SummaryTrack>(Pool);
                for (int i = 0; i < Pool; i++) { var c = usable[rnd.Next(usable.Count)]; if (c != from) pool.Add(c); }
                if (pool.Count < 2) continue;
                var cost = pool.Select(c => StoreSummary.CosineDistance(from.T36!, c.H36!)).ToList();
                randomSum += cost.Average();
                bestSum += cost.Min();
                keySum += cost[ArgMin(pool.Select(c => StoreSummary.CamelotDistance(from.WholeCode, c.WholeCode)).ToList())];
                edgeSum += cost[ArgMin(pool.Select(c => StoreSummary.CamelotDistance(from.TailCode, c.HeadCode)).ToList())];
                chromaSum += cost[ArgMin(pool.Select(c => StoreSummary.CosineDistance(from.W12!, c.W12!)).ToList())];
                n++;
            }
            if (n == 0) { o.Add("  pick rules: not computed (no trial had two candidates)"); return; }
            double rand = randomSum / n, best = bestSum / n, span = rand - best;
            if (span <= 0) { o.Add("  pick rules: not computed (every candidate scored the same)"); return; }
            o.Add($"  pick rule, {n} trials, pool {Pool}: whole key {F((rand - keySum / n) / span, 2)}, " +
                  $"edge key {F((rand - edgeSum / n) / span, 2)},");
            o.Add($"  whole-track chroma {F((rand - chromaSum / n) / span, 2)}");
        }

        static int ArgMin(List<double> xs)
        {
            int bi = 0; double b = xs[0];
            for (int i = 1; i < xs.Count; i++) if (xs[i] < b) { b = xs[i]; bi = i; }
            return bi;
        }

        // ---------------------------------------------------------------- part 4

        static void FeatureSpread(SqliteDb db, long contentRows, List<string> o)
        {
            o.Add("RANGE — this library's raw measurements vs the model's fitted range");
            if (contentRows == 0)
            {
                // One honest sentence beats 21 "not stored here" lines that blame the schema for an empty store.
                o.Add("  not computed: the store holds no tracks");
                o.Add("");
                return;
            }
            // The reference is provenance, not explanation: without the model and date these sigmas cannot be
            // compared with anyone else's, so it survives --store-summary-min. The caveat about it does not.
            Note(o, $"reference: {ModelTag}");
            Explain(o, "A hub set to another model (autoQ.model, or a mood-model json in its data folder) was fitted " +
                    "on a different range, and these sigmas are not its. Raw stored values, not the scaled " +
                    "valence/arousal, which clamp at the ends and would hide exactly this.");
            var data = new List<(Feat F, List<double> Values)>();
            foreach (var f in ModelScale) data.Add((f, ReadFeature(db, f.Name)));
            RangeTables(data, o);
        }

        /// <summary>The two range tables, apart from the store read so they are testable on plain numbers.
        ///
        /// <para>The first is the library's own raw statistics. n, mean and std are there so several libraries'
        /// blocks can be POOLED into one scale without anyone's tracks: a combined mean and standard deviation follow
        /// exactly from each block's three numbers. That is also why a feature the model has no fitted range for
        /// still gets its row here.</para>
        ///
        /// <para>The second is the same library in the model's fitted sigmas, at both ends. A median can sit inside
        /// the fitted range while a tail is far outside it, and the tails are where a model that fits a library badly
        /// misreads it — so both ends are printed, with the share of tracks past
        /// <see cref="ValenceInputBound"/> each way.</para></summary>
        internal static void RangeTables(IReadOnlyList<(Feat F, List<double> Values)> data, List<string> o)
        {
            var raw = new List<string>(); var sigma = new List<string>();
            var absent = new List<string>(); var noScale = new List<string>();
            foreach (var (f, values) in data)
            {
                if (values.Count == 0) { absent.Add(f.Name); continue; }
                var vals = new List<double>(values);
                vals.Sort();
                int n = vals.Count;
                double p5 = StoreSummary.Percentile(vals, 5), p50 = StoreSummary.Percentile(vals, 50), p95 = StoreSummary.Percentile(vals, 95);
                raw.Add("  " + f.Name.PadRight(18) + " " + n.ToString(CultureInfo.InvariantCulture).PadRight(7) + " " +
                        Col(G(p5)) + Col(G(p50)) + Col(G(p95)) + Col(G(StoreSummary.Mean(vals))) + G(StoreSummary.Std(vals)));

                if (!HasUsableScale(f)) { noScale.Add(f.Name); continue; }
                var tail = StoreSummary.TailCounts(vals, f.Mean, f.Std, ValenceInputBound);
                sigma.Add("  " + f.Name.PadRight(18) + " " +
                          Sig((p5 - f.Mean) / f.Std).PadRight(7) + Sig((p50 - f.Mean) / f.Std).PadRight(7) + Sig((p95 - f.Mean) / f.Std).PadRight(7) +
                          Pct2(tail.Under, n).PadRight(10) + Pct2(tail.Over, n));
            }

            if (raw.Count > 0)
            {
                o.Add("  " + "feature".PadRight(18) + " " + "n".PadRight(7) + " " + Col("p5") + Col("p50") + Col("p95") + Col("mean") + "std");
                o.AddRange(raw);
            }
            if (sigma.Count > 0)
            {
                string bound = ValenceInputBound.ToString("0.##", CultureInfo.InvariantCulture);
                o.Add("  " + "in fitted sigmas".PadRight(18) + " " + "p5".PadRight(7) + "p50".PadRight(7) + "p95".PadRight(7) +
                      ("under -" + bound).PadRight(10) + "over +" + bound);
                o.AddRange(sigma);
            }
            if (absent.Count > 0)
                // Wrapped, like the noScale list below it: a sparse store names enough features here to run the line
                // to 160 characters, and this is state a reader needs, so it cannot just be dropped either.
                Note(o, "no value stored in this library: " + string.Join(", ", absent));
            if (noScale.Count > 0)
                Note(o, "no fitted range to compare against, the model's scale for it being mean 0 / std 1: " + string.Join(", ", noScale));
            Explain(o, "Sigma is distance from the fitted mean in fitted standard deviations. A p5 or p95 past about 2 " +
                    "sigma means that end of this library sits outside most of what the model was trained on. This " +
                    "model's valence side stops reading an input at 4 sigma, so a track past it is scored as if it " +
                    "sat at 4, while its arousal side reads the full value; the last two columns are the share of " +
                    "tracks out there. n, mean and std are enough to pool several libraries into one scale without " +
                    "their tracks. Mean and sigma are not a min and a max, so this bounds the comparison rather than " +
                    "proving a value was never seen.");
            o.Add("");
        }

        static string Col(string s) => s.PadRight(8) + " ";

        /// <summary>A share to two places: the tails this is used for are a fraction of a percent of a library, and one
        /// place would print most of them as 0.0%.</summary>
        static string Pct2(int k, int n) => n == 0 ? "n/a" : (100.0 * k / n).ToString("0.00", CultureInfo.InvariantCulture) + "%";

        static List<double> ReadFeature(SqliteDb db, string name)
        {
            var vals = new List<double>();
            long scale; int dp;
            try { scale = FieldPrecision.Scale(name); dp = FieldPrecision.Dp(name); }
            catch (InvalidOperationException) { return vals; }      // not a field this build knows
            try
            {
                using var st = db.Prepare($"SELECT \"{name}\" FROM track_content WHERE \"{name}\" IS NOT NULL");
                while (st.Step()) vals.Add(Math.Round((double)st.GetInt64(0) / scale, dp));
            }
            catch (SqliteException) { return new List<double>(); }  // not a column in this store's schema
            return vals;
        }

        // ---------------------------------------------------------------- loading

        static List<SummaryTrack> LoadTracks(SqliteDb db, string edgeVersion, bool hasEdgeTable,
                                             out int wholeKeyRows, out int edgeRows, out int chromaRows)
        {
            var list = new List<SummaryTrack>();
            wholeKeyRows = edgeRows = chromaRows = 0;

            // A store older than schema v4 has no edge table to join at all; read the whole-track half so the key
            // agreement and the feature range still answer, instead of failing the whole report on a missing table.
            if (!hasEdgeTable) return LoadTracksWholeOnly(db, ref wholeKeyRows);

            // LEFT JOIN, so a store with no edges still answers part 1 and part 4 instead of refusing.
            var sql = @"SELECT t.""key"", t.mode,
                               t.keyVotes_krumhansl_key, t.keyVotes_krumhansl_scale, t.keyVotes_krumhansl_strength,
                               t.keyVotes_temperley_key, t.keyVotes_temperley_scale, t.keyVotes_temperley_strength,
                               t.keyVotes_edma_key, t.keyVotes_edma_scale, t.keyVotes_edma_strength,
                               t.chordsKey, t.chordsScale, t.hpcp12,
                               h.key_code, h.key_agreement, h.key_votes, h.hpcp,
                               e.key_code, e.key_agreement, e.key_votes, e.hpcp
                        FROM track_content t
                        LEFT JOIN edge h ON h.content_hash = t.audioStreamSha256 AND h.kind = 'head'
                             AND h.status IN ('ok','short-context') AND h.producer_version = ?
                        LEFT JOIN edge e ON e.content_hash = t.audioStreamSha256 AND e.kind = 'tail'
                             AND e.status IN ('ok','short-context') AND e.producer_version = ?";

            using var st = db.Prepare(sql);
            st.Bind(1, edgeVersion).Bind(2, edgeVersion);
            while (st.Step())
            {
                var t = new SummaryTrack();
                string? flatKey = st.GetString(0), flatMode = st.GetString(1);
                var votes = new List<(string?, string?, bool)>
                {
                    (st.GetString(2), st.GetString(3), !st.IsNull(4) && !Empty(st.GetString(2))),
                    (st.GetString(5), st.GetString(6), !st.IsNull(7) && !Empty(st.GetString(5))),
                    (st.GetString(8), st.GetString(9), !st.IsNull(10) && !Empty(st.GetString(8))),
                };
                t.WholeCode = StoreSummary.CamelotOf(flatKey, flatMode);
                if (t.WholeCode != null) wholeKeyRows++;

                var flat = StoreSummary.AgreementWith(flatKey, flatMode, votes);
                t.FlatAgree = flat.Agreeing; t.FlatPresent = flat.Present;
                var edma = StoreSummary.AgreementWith(st.GetString(8), st.GetString(9), votes);
                t.EdmaAgree = edma.Agreeing; t.EdmaPresent = edma.Present;
                t.Provenance = StoreSummary.KeyProvenance(flatKey, flatMode, st.GetString(8), st.GetString(9),
                                                          st.GetString(2), st.GetString(3), st.GetString(11), st.GetString(12));

                t.W12 = StoreSummary.Unit(DecodeInt32(st.GetBlob(13)));
                t.HeadCode = Blank(st.GetString(14)); t.HeadAgree = (int)st.GetInt64(15); t.HeadPresent = (int)st.GetInt64(16);
                t.H36 = StoreSummary.Unit(DecodeFloat32(st.GetBlob(17)));
                t.TailCode = Blank(st.GetString(18)); t.TailAgree = (int)st.GetInt64(19); t.TailPresent = (int)st.GetInt64(20);
                t.T36 = StoreSummary.Unit(DecodeFloat32(st.GetBlob(21)));

                if (t.HeadCode != null && t.TailCode != null) edgeRows++;
                if (t.W12 != null && t.H36 != null && t.T36 != null) chromaRows++;
                list.Add(t);
            }
            return list;
        }

        /// <summary>The whole-track half only, for a store below schema v4 (no edge table).</summary>
        static List<SummaryTrack> LoadTracksWholeOnly(SqliteDb db, ref int wholeKeyRows)
        {
            var list = new List<SummaryTrack>();
            using var st = db.Prepare(@"SELECT t.""key"", t.mode,
                                               t.keyVotes_krumhansl_key, t.keyVotes_krumhansl_scale, t.keyVotes_krumhansl_strength,
                                               t.keyVotes_temperley_key, t.keyVotes_temperley_scale, t.keyVotes_temperley_strength,
                                               t.keyVotes_edma_key, t.keyVotes_edma_scale, t.keyVotes_edma_strength,
                                               t.chordsKey, t.chordsScale, t.hpcp12
                                        FROM track_content t");
            while (st.Step())
            {
                var t = new SummaryTrack();
                string? flatKey = st.GetString(0), flatMode = st.GetString(1);
                var votes = new List<(string?, string?, bool)>
                {
                    (st.GetString(2), st.GetString(3), !st.IsNull(4) && !Empty(st.GetString(2))),
                    (st.GetString(5), st.GetString(6), !st.IsNull(7) && !Empty(st.GetString(5))),
                    (st.GetString(8), st.GetString(9), !st.IsNull(10) && !Empty(st.GetString(8))),
                };
                t.WholeCode = StoreSummary.CamelotOf(flatKey, flatMode);
                if (t.WholeCode != null) wholeKeyRows++;
                var flat = StoreSummary.AgreementWith(flatKey, flatMode, votes);
                t.FlatAgree = flat.Agreeing; t.FlatPresent = flat.Present;
                var edma = StoreSummary.AgreementWith(st.GetString(8), st.GetString(9), votes);
                t.EdmaAgree = edma.Agreeing; t.EdmaPresent = edma.Present;
                t.Provenance = StoreSummary.KeyProvenance(flatKey, flatMode, st.GetString(8), st.GetString(9),
                                                          st.GetString(2), st.GetString(3), st.GetString(11), st.GetString(12));
                t.W12 = StoreSummary.Unit(DecodeInt32(st.GetBlob(13)));
                list.Add(t);
            }
            return list;
        }

        static bool Empty(string? s) => string.IsNullOrWhiteSpace(s);
        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        /// <summary>The edge's HPCP: 36 float32 little-endian (v4.sql). Any other length is not this vector.</summary>
        internal static double[]? DecodeFloat32(byte[]? b)
        {
            if (b == null || b.Length == 0 || b.Length % 4 != 0) return null;
            var o = new double[b.Length / 4];
            for (int i = 0; i < o.Length; i++) o[i] = BitConverter.ToSingle(b, i * 4);
            return o;
        }

        /// <summary>hpcp12: int32 scaled by FieldPrecision. The scale is uniform across the bins, so a unit-scaled
        /// vector is identical whether or not it is unscaled first — the cosine does not care.</summary>
        internal static double[]? DecodeInt32(byte[]? b)
        {
            if (b == null || b.Length == 0 || b.Length % 4 != 0) return null;
            var o = new double[b.Length / 4];
            for (int i = 0; i < o.Length; i++) o[i] = BitConverter.ToInt32(b, i * 4);
            return o;
        }

        static string Meta(SqliteDb db, string key)
        {
            try
            {
                using var st = db.Prepare("SELECT value FROM store_meta WHERE key = ?");
                st.Bind(1, key);
                return st.Step() ? (st.GetString(0) ?? "") : "";
            }
            catch (SqliteException) { return ""; }
        }

        static long Count(SqliteDb db, string sql)
        {
            try { using var st = db.Prepare(sql); return st.Step() ? st.GetInt64(0) : 0; }
            catch (SqliteException) { return 0; }
        }

        /// <summary>Block width. Deliberately FIXED rather than <c>Program.ReportWidth</c>, which follows the console:
        /// two people posting the same store should post the same block, and a width that varies with a window makes
        /// two posts differently wrapped and harder to read side by side.</summary>
        internal const int BlockWidth = 78;

        /// <summary>A sentence wrapped to the block width, through the writer the scan summary already uses so there
        /// is one wrapping rule in the exe rather than two.
        ///
        /// <para>Note is for what the store HOLDS — a count, a shortfall, what the numbers are measured against. It
        /// survives <c>--store-summary-min</c>, because nobody can read a missing section or a sigma without it.
        /// Prose that explains what a number MEANS goes through <see cref="Explain"/>.</para></summary>
        static void Note(List<string> o, string text, string indent = "  ")
        {
            foreach (var l in Program.WrapNote(text, BlockWidth - indent.Length)) o.Add(indent + l);
        }

        /// <summary>Set false by <c>--store-summary-min</c>. Every row stays; only the sentences explaining what a
        /// row means are dropped — someone reading many blocks from different libraries already knows.</summary>
        internal static bool Explanations = true;

        /// <summary>A sentence that explains what a row MEANS. Dropped by <c>--store-summary-min</c>; everything it
        /// writes is prose, so dropping it can never cost a number.</summary>
        static void Explain(List<string> o, string text, string indent = "  ")
        {
            if (!Explanations) return;
            Note(o, text, indent);
        }

        /// <summary>A SQLite message without the statement it quotes: the SQL is noise to the person running this.</summary>
        static string FirstLine(string msg)
        {
            int b = msg.IndexOf(" [", StringComparison.Ordinal);
            var s = b > 0 ? msg.Substring(0, b) : msg;
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            return nl > 0 ? s.Substring(0, nl) : s;
        }

        static string Pct(int k, int n) => n == 0 ? "n/a" : $"{100.0 * k / n:0.0}%";
        static string F(double v, int dp) => double.IsNaN(v) ? "n/a" : v.ToString("F" + dp.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        /// <summary>A sigma to one place with its sign, and zero with none. The sign is taken AFTER rounding: a value
        /// just under zero used to print "0.0" beside an exact zero's "+0.0" in the same column.</summary>
        internal static string Sig(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "n/a";
            var magnitude = Math.Abs(v).ToString("F1", CultureInfo.InvariantCulture);
            return magnitude == "0.0" ? magnitude : (v > 0 ? "+" : "-") + magnitude;
        }

        static string G(double v)
        {
            if (double.IsNaN(v)) return "n/a";
            double a = Math.Abs(v);
            if (a != 0 && (a < 0.001 || a >= 100000)) return v.ToString("G3", CultureInfo.InvariantCulture);
            return v.ToString(a >= 100 ? "F1" : a >= 1 ? "F3" : "F5", CultureInfo.InvariantCulture);
        }
    }
}
