using System;
using System.Collections.Concurrent;

namespace Truedat
{
    /// <summary>
    /// Hand-rolled radix-2 Cooley-Tukey FFT for real-input spectral analysis.
    /// Used by ComputeHfAnalysis (Phase 5) to distinguish genuine broadband
    /// hi-res content from ffmpeg-upsampled fake hi-res whose energy is
    /// concentrated in narrow mirror spikes.
    ///
    /// Forward() runs an in-place complex FFT on real input padded with zero
    /// imaginary parts. Size must be a power of two; ~75 µs per call at size
    /// 4096 on commodity hardware.
    ///
    /// Pure managed; no native interop, no NuGet refs. Single-threaded per
    /// call but reentrant — the only shared state is the Hann window cache,
    /// which uses ConcurrentDictionary so concurrent callers at the same
    /// size share the cached window without contention.
    /// </summary>
    internal static class Fft
    {
        private static readonly ConcurrentDictionary<int, double[]> _hannCache =
            new ConcurrentDictionary<int, double[]>();

        /// <summary>Forward DFT in place. real[k] / imag[k] become the kth
        /// frequency-bin's real and imaginary components on return. Throws
        /// ArgumentException if the inputs aren't the same power-of-two length.</summary>
        public static void Forward(double[] real, double[] imag)
        {
            if (real == null) throw new ArgumentNullException(nameof(real));
            if (imag == null) throw new ArgumentNullException(nameof(imag));
            if (real.Length != imag.Length)
                throw new ArgumentException("real and imag length must match");
            int n = real.Length;
            if (n < 2 || (n & (n - 1)) != 0)
                throw new ArgumentException("size must be a power of two >= 2");

            // Bit-reversal permutation.
            int j = 0;
            for (int i = 0; i < n - 1; i++)
            {
                if (i < j)
                {
                    var tr = real[i]; real[i] = real[j]; real[j] = tr;
                    var ti = imag[i]; imag[i] = imag[j]; imag[j] = ti;
                }
                int k = n >> 1;
                while (k <= j) { j -= k; k >>= 1; }
                j += k;
            }

            // Butterfly stages — w recurrence avoids per-bin trig.
            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1;
                double theta = -2.0 * Math.PI / len;
                double wpr = Math.Cos(theta);
                double wpi = Math.Sin(theta);
                for (int i = 0; i < n; i += len)
                {
                    double wr = 1.0, wi = 0.0;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k;
                        int b = a + half;
                        double tr = wr * real[b] - wi * imag[b];
                        double ti = wr * imag[b] + wi * real[b];
                        real[b] = real[a] - tr;
                        imag[b] = imag[a] - ti;
                        real[a] += tr;
                        imag[a] += ti;
                        double nwr = wr * wpr - wi * wpi;
                        wi = wr * wpi + wi * wpr;
                        wr = nwr;
                    }
                }
            }
        }

        /// <summary>Hann window of the requested size. Cached per-size; the
        /// same array reference returns on every call so allocation is amortized
        /// across all FFT windows in a track.</summary>
        public static double[] Hann(int size)
        {
            if (size < 2) throw new ArgumentException("size must be >= 2", nameof(size));
            return _hannCache.GetOrAdd(size, sz =>
            {
                var w = new double[sz];
                double scale = 2.0 * Math.PI / (sz - 1);
                for (int i = 0; i < sz; i++)
                    w[i] = 0.5 * (1.0 - Math.Cos(scale * i));
                return w;
            });
        }

        /// <summary>Magnitude-squared per bin: mag2[k] = real[k]^2 + imag[k]^2.
        /// Caller-allocated output to avoid per-call allocation in inner loops.</summary>
        public static void Magnitude2(double[] real, double[] imag, double[] mag2)
        {
            if (real == null) throw new ArgumentNullException(nameof(real));
            if (imag == null) throw new ArgumentNullException(nameof(imag));
            if (mag2 == null) throw new ArgumentNullException(nameof(mag2));
            int n = real.Length;
            if (imag.Length != n || mag2.Length != n)
                throw new ArgumentException("array lengths must match");
            for (int i = 0; i < n; i++)
                mag2[i] = real[i] * real[i] + imag[i] * imag[i];
        }
    }

    /// <summary>Phase 5 — FFT-derived spectral-structure metrics for the
    /// high-frequency band (≥ 22050 Hz). Three discriminators:
    ///
    /// - <see cref="Flatness"/>: Wiener entropy of HF bins. High = broadband
    ///   noise-like (real hi-res); low = peaky (imaging artifact).
    /// - <see cref="PeakToMean"/>: max(HF mag²) / mean(HF mag²). Catches
    ///   narrow spikes that flatness can dilute against a noise floor.
    /// - <see cref="ImagingSymmetry"/>: Pearson correlation of HF mag² with
    ///   its mirror about the original 22050 Hz Nyquist — ffmpeg upsampling
    ///   literally reflects the source band, so correlation → 0.7–0.95;
    ///   genuine HF content is uncorrelated → 0.0–0.2.
    ///
    /// Populated by ComputeHfAnalysis when the source is &gt; 44.1 kHz and
    /// ffmpeg is available; null on legacy entries, sub-44.1 sources, or
    /// when analysis fails. Public because TrackFeatures.HfSpectralStructure
    /// is part of the serialized mbxmoods.json contract.</summary>
    public sealed class HfSpectralStructure
    {
        public double Flatness;
        public double PeakToMean;
        public double ImagingSymmetry;
        public string Method = "";
    }
}
