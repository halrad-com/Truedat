using System;
using System.Collections.Generic;
using System.Linq;

namespace Truedat
{
    /// <summary>Which tracks a scan leaves for a second pass, so that the same audio is not analyzed twice.
    ///
    /// Two copies of the same audio at different paths were each analyzed in full when they were both new to the scan. The scan
    /// already knows how to reuse an analysis for the same audio at another path — it is how a moved file is recognized — but only
    /// once the first copy's analysis exists. So a track that could be a copy of one before it waits for a second pass, and is
    /// checked against what the first pass analyzed.
    ///
    /// "Could be a copy" is an exact length: copies of the same audio are always the same length, the library already records
    /// each track's length, and nothing has to be read from disk to compare. Most tracks that share a length are not copies; they
    /// lose nothing by waiting, and are analyzed in the second pass as they would have been in the first.</summary>
    internal static class ScanOrder
    {
        internal static void SplitRepeatedLengths<T>(IReadOnlyList<T> items, Func<T, long> lengthMs, out List<T> first, out List<T> again)
        {
            first = new List<T>(items.Count);
            again = new List<T>();
            var seen = new HashSet<long>();
            foreach (var item in items)
            {
                long ms = lengthMs(item);
                // An unknown length matches nothing, so it never waits.
                if (ms > 0 && !seen.Add(ms)) again.Add(item);
                else first.Add(item);
            }
        }
    }

    /// <summary>The order a scan takes its tracks in, so that long tracks get the memory they need, do not all run at once, and do
    /// not start last.
    ///
    /// The extractors share one RAM budget (<see cref="SpoolBudget"/>) and the first track to ask for room gets it. Taken in library
    /// order, an album of DJ mixes arrives together: the first few get room and the rest decode once per pass. And a scan ends when
    /// its last track ends, so a long track started last adds its whole length to the end of the run.
    ///
    /// So a few workers (the long lanes) always take the longest track left, and the rest take the library in order. The tracks too
    /// big for an even split of the budget — the long set — are left to the long lanes, and there are only as many long lanes as the
    /// budget can hold such tracks at once. Whichever group runs out of work helps the other, so no worker waits while there is work.
    ///
    /// Nothing here is a fixed length. What counts as long, and how many run together, come from the free RAM, the number of
    /// workers and the library being scanned.</summary>
    internal sealed class LongFirstQueue<T>
    {
        /// <summary>The bytes a second of audio decodes to when nothing better is known: 44.1 kHz stereo, which is also what a
        /// higher-rate file is converted to before analysis.</summary>
        internal const long TypicalPcmBytesPerSec = 44100L * 2 * 4;

        readonly object _gate = new object();
        readonly T[] _items;
        readonly bool[] _isLong, _taken;
        readonly int[] _byLength;      // every track, longest first; the long set is its head
        readonly int _longCount, _lanes;
        int _nextByLength, _nextLibrary, _longLanesBusy, _left, _ordinaryLeft;

        /// <summary>What a track of this length holds while it is analyzed, if it is held in memory.</summary>
        internal static long NeedBytes(double seconds)
            => SpoolBudget.ExtractorBytesFor(seconds) + SpoolBudget.SpoolBytesFor(seconds, TypicalPcmBytesPerSec);

        internal LongFirstQueue(IReadOnlyList<T> items, Func<T, double> seconds, int workers, long budgetBytes)
        {
            int n = items.Count, w = Math.Max(1, workers);
            _items = new T[n];
            var secs = new double[n];
            for (int i = 0; i < n; i++)
            {
                _items[i] = items[i];
                double s = seconds(items[i]);
                secs[i] = s > 0 && !double.IsNaN(s) && !double.IsInfinity(s) ? s : 0;
            }
            _isLong = new bool[n];
            _taken = new bool[n];
            _byLength = Enumerable.Range(0, n).OrderByDescending(i => secs[i]).ToArray();   // stable: equal lengths keep library order
            _left = n;

            // The long set: tracks that need more than an even split of the budget. With no budget nothing is held in memory, so
            // there is nothing to make room for.
            if (w > 1 && budgetBytes > 0)
            {
                long evenShare = budgetBytes / w;
                foreach (int i in _byLength)
                {
                    if (secs[i] <= 0 || NeedBytes(secs[i]) <= evenShare) break;
                    _isLong[i] = true;
                    _longCount++;
                }
            }
            _ordinaryLeft = n - _longCount;

            // One worker has nothing to stagger against and takes the library in order. Otherwise at least one worker takes the
            // longest track left, and as many do as the budget holds long-set tracks at once, beside ordinary tracks on the rest.
            _lanes = w > 1 ? 1 : 0;
            if (_longCount > 0)
            {
                double longNeed = _byLength.Take(_longCount).Average(i => (double)NeedBytes(secs[i]));
                double ordinaryNeed = _ordinaryLeft > 0 ? _byLength.Skip(_longCount).Average(i => (double)NeedBytes(secs[i])) : 0;
                double spare = budgetBytes - w * ordinaryNeed, each = longNeed - ordinaryNeed;
                if (spare > 0 && each > 0) _lanes = (int)Math.Max(1, Math.Min(w, Math.Floor(spare / each)));
            }
        }

        internal int Count => _items.Length;
        internal int LongCount => _longCount;
        /// <summary>How many workers take the longest track left while there is other work.</summary>
        internal int Lanes => _lanes;
        internal bool IsLong(int index) => _isLong[index];

        /// <summary>The next track for a worker that is free. <paramref name="longLane"/> says whether it was taken as the longest
        /// left, and is handed back to <see cref="Done"/> when the track finishes.</summary>
        internal bool TryTake(out T item, out bool longLane)
        {
            lock (_gate)
            {
                if (_left == 0) { item = default!; longLane = false; return false; }
                // A long lane is free, or only long-set tracks remain: the longest left. Otherwise the next ordinary track in
                // library order.
                bool longest = _longLanesBusy < _lanes || _ordinaryLeft == 0;
                int index;
                if (longest)
                {
                    while (_taken[_byLength[_nextByLength]]) _nextByLength++;
                    index = _byLength[_nextByLength++];
                    _longLanesBusy++;
                }
                else
                {
                    while (_taken[_nextLibrary] || _isLong[_nextLibrary]) _nextLibrary++;
                    index = _nextLibrary++;
                }
                _taken[index] = true;
                _left--;
                if (!_isLong[index]) _ordinaryLeft--;
                item = _items[index];
                longLane = longest;
                return true;
            }
        }

        internal void Done(bool longLane)
        {
            if (!longLane) return;
            lock (_gate) _longLanesBusy--;
        }

        /// <summary>The tracks in the order free workers would take them, one after another.</summary>
        internal IEnumerable<KeyValuePair<T, bool>> Feed()
        {
            while (TryTake(out var item, out var longLane))
                yield return new KeyValuePair<T, bool>(item, longLane);
        }
    }
}
