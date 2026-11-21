using System;

namespace Truedat
{
    /// <summary>One RAM budget shared by every track being analyzed, for the decoded audio the extractor holds in memory so its later
    /// passes do not decode the file again. The budget is a share of the RAM that was free when the scan began; a track asks for the
    /// room its own audio needs and gets it if the room is there — first to ask wins. A track that does not get it is not delayed: its
    /// extractor decodes once per pass, as every track did before the spool existed, and writes the same output.
    ///
    /// This replaces a fixed share per worker. That rule assumed every worker needed its whole share at the same moment, so one long
    /// track was refused room that the short tracks beside it were not using.</summary>
    internal sealed class SpoolBudget
    {
        /// <summary>The part of the free RAM the scan's extractors may use between them.</summary>
        internal const double Share = 0.6;
        /// <summary>The extractor's own memory for a track: a base plus a cost per second of audio (measured on the current build).</summary>
        internal const double BaseMiB = 109.1, ExtractorMiBPerSec = 0.0656;
        /// <summary>Room asked for beyond the audio itself: the spool allocates in 4 MiB blocks and keeps a size per packet, and a
        /// track that runs out of room part-way has recorded for nothing. Kept small on purpose: a wide margin on every track is room
        /// the smallest machine does not have.</summary>
        internal const double SpoolMargin = 1.02;
        internal const long SpoolSlackBytes = 8L << 20;
        const double MiB = 1048576.0;

        readonly object _gate = new object();
        readonly long _total;
        long _reserved;

        internal SpoolBudget(long availableRamBytes) { _total = availableRamBytes > 0 ? (long)(Share * availableRamBytes) : 0; }

        internal long TotalBytes => _total;
        internal long ReservedBytes { get { lock (_gate) return _reserved; } }

        /// <summary>The run's account: tracks given room, tracks that asked and found none, and tracks that could not ask because
        /// their length or format was not readable.</summary>
        internal int Held { get { lock (_gate) return _held; } }
        internal int Refused { get { lock (_gate) return _refused; } }
        internal int Unknown { get { lock (_gate) return _unknown; } }
        int _held, _refused, _unknown;

        /// <summary>One line for the scan summary; null when no track asked.</summary>
        internal string? SummaryLine()
        {
            lock (_gate)
            {
                if (_held + _refused + _unknown == 0) return null;
                string line = $"{_held:N0} track(s) held in memory for analysis";
                if (_refused > 0) line += $", {_refused:N0} decoded once per pass for lack of room";
                if (_unknown > 0) line += $", {_unknown:N0} of unreadable length";
                return line + $"  ({_total / 1073741824.0:F1} GB shared)";
            }
        }

        /// <summary>What one track holds while it is analyzed. <see cref="SpoolBytes"/> is the cap handed to the extractor; zero means
        /// it decodes once per pass.</summary>
        internal struct Grant
        {
            internal long ExtractorBytes;
            internal long SpoolBytes;
        }

        internal static long ExtractorBytesFor(double durationSec)
            => (long)((BaseMiB + ExtractorMiBPerSec * Math.Max(0, durationSec)) * MiB);

        /// <summary>The room a track's decoded audio needs: its length times the bytes a second of it decodes to, plus the margin.
        /// Zero when either is unknown — a track of unknown length is not held in memory.</summary>
        internal static long SpoolBytesFor(double durationSec, long pcmBytesPerSec)
            => durationSec > 0 && pcmBytesPerSec > 0 ? (long)(durationSec * pcmBytesPerSec * SpoolMargin) + SpoolSlackBytes : 0;

        /// <summary>Takes this track's room out of the budget. The extractor's own memory is always counted, because the extractor
        /// runs whatever is decided here; the room for the decoded audio is granted whole or not at all, since part of a track held in
        /// memory saves nothing.</summary>
        internal Grant Reserve(double durationSec, long pcmBytesPerSec)
        {
            long ext = ExtractorBytesFor(durationSec);
            long want = _total > 0 ? SpoolBytesFor(durationSec, pcmBytesPerSec) : 0;
            lock (_gate)
            {
                long spool = want > 0 && _reserved + ext + want <= _total ? want : 0;
                if (spool > 0) _held++; else if (want > 0) _refused++; else _unknown++;
                _reserved += ext + spool;
                return new Grant { ExtractorBytes = ext, SpoolBytes = spool };
            }
        }

        internal void Release(Grant g)
        {
            lock (_gate) _reserved -= g.ExtractorBytes + g.SpoolBytes;
        }
    }
}
