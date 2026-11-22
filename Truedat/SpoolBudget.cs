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
        /// <summary>The extractor decides for itself whether a track fits the room it was given: it refuses when the track's length
        /// times the bytes a second decodes to, PLUS 5%, is over the cap (audioloader.cpp, startRecording). A grant smaller than that
        /// is a track counted here as held in memory that the extractor then decodes once per pass. This is that factor; the two must
        /// stay equal.</summary>
        internal const double ExtractorCheckFactor = 1.05;
        /// <summary>The length read from a file's header can be a little shorter than the one the extractor reads from the stream.</summary>
        internal const double LengthTolerance = 1.02;
        /// <summary>The spool allocates in 4 MiB blocks and keeps a size per packet.</summary>
        internal const long SpoolSlackBytes = 8L << 20;
        const double MiB = 1048576.0;

        readonly object _gate = new object();
        readonly long _total;
        long _reserved;

        /// <param name="workers">How many extractors the run starts at once.</param>
        /// <param name="typicalSeconds">The length of an ordinary track in this run: what a worker that has not started yet is
        /// expected to analyze next.</param>
        internal SpoolBudget(long availableRamBytes, int workers = 1, double typicalSeconds = 270)
        {
            _total = availableRamBytes > 0 ? (long)(Share * availableRamBytes) : 0;
            _workers = Math.Max(1, workers);
            _typicalExtractorBytes = ExtractorBytesFor(typicalSeconds > 0 ? typicalSeconds : 270);
        }
        readonly int _workers;
        readonly long _typicalExtractorBytes;
        int _running;

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
            => durationSec > 0 && pcmBytesPerSec > 0 ? (long)(durationSec * pcmBytesPerSec * ExtractorCheckFactor * LengthTolerance) + SpoolSlackBytes : 0;

        /// <summary>Takes this track's room out of the budget. The extractor's own memory is always counted, because the extractor
        /// runs whatever is decided here; the room for the decoded audio is granted whole or not at all, since part of a track held in
        /// memory saves nothing. And it is granted only if room is left for the extractors of the workers that have not started a
        /// track yet: they will run too, and without that an early track takes the room and the later extractors push what is in use
        /// over the budget.</summary>
        internal Grant Reserve(double durationSec, long pcmBytesPerSec)
        {
            long ext = ExtractorBytesFor(durationSec);
            long want = _total > 0 ? SpoolBytesFor(durationSec, pcmBytesPerSec) : 0;
            lock (_gate)
            {
                // Sized on the larger of an ordinary track and this one: a list or folder scan knows no typical length, and the
                // tracks running beside a long one are often long too (long tracks are taken first, side by side).
                long othersToCome = (long)Math.Max(0, _workers - _running - 1) * Math.Max(_typicalExtractorBytes, ext);
                long spool = want > 0 && _reserved + ext + want + othersToCome <= _total ? want : 0;
                if (spool > 0) _held++; else if (want > 0) _refused++; else _unknown++;
                _reserved += ext + spool;
                _running++;
                return new Grant { ExtractorBytes = ext, SpoolBytes = spool };
            }
        }

        internal void Release(Grant g)
        {
            lock (_gate) { _reserved -= g.ExtractorBytes + g.SpoolBytes; if (_running > 0) _running--; }
        }
    }
}
