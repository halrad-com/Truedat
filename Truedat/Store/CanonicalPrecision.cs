using System;
using System.Globalization;

namespace Truedat.Store
{
    /// <summary>The store's one number rule (operator, 2026-10-12): every floating-point feature is held and exported at
    /// SIX significant figures. Essentia computes in float32 (7 significant decimal digits at most, 6 guaranteed to round-trip),
    /// so the 6th figure is the last one that is measurement; past it the catalog's text carries float→double→text noise
    /// ("0.022454000000000002") that costs bytes and breaks equality between two renderings of one value. The rule lives in the
    /// store path only — the scan's catalog writer and its bytes are the hub's contract and are untouched.</summary>
    internal static class CanonicalPrecision
    {
        internal const int SignificantFigures = 6;
        static readonly decimal One = 1.0000000000000000000000000000m;   // dividing by it strips a decimal's trailing zeros

        /// <summary>The nearest double to <paramref name="v"/> rounded to six significant figures; 0, NaN and infinities pass through.</summary>
        internal static double Sig6(double v)
        {
            if (v == 0 || double.IsNaN(v) || double.IsInfinity(v)) return v;
            return (double)Exact(v);
        }

        /// <summary>The canonical value as an exact decimal, no trailing zeros ("0.022454", "3600110", "-0.000000005555").</summary>
        internal static decimal Exact(double v)
        {
            if (v == 0 || double.IsNaN(v) || double.IsInfinity(v)) return 0m;
            int magnitude = (int)Math.Floor(Math.Log10(Math.Abs(v)));          // 1293.3 -> 3; 0.022 -> -2
            int decimals = SignificantFigures - 1 - magnitude;                   // figures to keep after the point (may be negative)
            if (Math.Abs(v) >= 7.9e28) return (decimal)Math.Sign(v) * 79000000000000000000000000000m;   // beyond decimal: unreachable for audio features
            decimal d = (decimal)v;                                              // <= 15 significant digits, exact for our purposes
            decimal r;
            if (decimals >= 0)
            {
                r = decimal.Round(d, Math.Min(28, decimals), MidpointRounding.AwayFromZero);
            }
            else
            {
                decimal scale = Pow10(-decimals);                                // round to a multiple of 10^-decimals
                r = decimal.Round(d / scale, 0, MidpointRounding.AwayFromZero) * scale;
            }
            // A rounding that carried into a new leading digit (999999.5 -> 1000000) still has six figures: nothing to redo.
            return r / One;
        }

        internal static string Text(double v) => Exact(v).ToString(CultureInfo.InvariantCulture);

        static decimal Pow10(int n) { decimal p = 1m; for (int i = 0; i < n; i++) p *= 10m; return p; }
    }
}
