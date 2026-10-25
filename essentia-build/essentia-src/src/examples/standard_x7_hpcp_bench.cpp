/*
 * truedat x7 HPCP sizing bench (measurement tooling, not part of any shipped
 * binary). Answers two questions about the music extractor's three HPCPs
 * (MusicTonalDescriptors.cpp: key 36 bins, chords 36, tuning 120) before any
 * change is made to them:
 *
 *   1. A FUSED chords+tuning HPCP would walk the spectral peaks and their
 *      harmonic positions once. Chords and tuning are configured identically
 *      except size, so per (peak, harmonic position) the pow() that finds the
 *      hypothesised fundamental and the log2() that places it are the same
 *      value in both; only the per-bin cosine windows differ (~1.5 bins for
 *      36, ~5 for 120). The saving is chords' share of that walk - timed here
 *      alone ("walk"), next to the real algorithm's full time ("hpcp").
 *   2. HPCP::compute heap-allocates hpcp_LO and hpcp_HI on every call when
 *      bandPreset is on (chords and tuning). Member buffers would remove that
 *      without changing a result - its cost is timed here alone ("alloc").
 *
 * Input: the extractor's own tonal front end in standard mode - MonoLoader at
 * 44.1 kHz (production inputs are 44.1 kHz), FrameCutter 4096/2048, Windowing
 * blackmanharris62, Spectrum, SpectralPeaks maxPeaks 60 / magnitudeThreshold
 * 1e-5 / 20-3500 Hz / orderBy magnitude. The standard FrameCutter has no
 * silentFrames parameter, so silent frames are not noise-filled as the
 * extractor's streaming one does ("noise"): they give few or no peaks here,
 * which under-counts only tracks with digital silence.
 * The extractor feeds these from the replay-gain-scaled signal; this bench
 * does not apply the gain, which can move a peak across the 1e-5 threshold
 * only at the margin. referenceFrequency is 440 Hz (the extractor uses the
 * track's measured tuning; the cost does not depend on it).
 * Each timing is the minimum over R repeats of a pass over every frame.
 *
 *   essentia_standard_x7_hpcp_bench <input> [repeats=5]
 *
 * Prints one line per measurement, key=value, to stdout.
 */

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <vector>
#include <essentia/algorithmfactory.h>
#include <essentia/utils/x6profile.h>

using namespace std;
using namespace essentia;
using namespace essentia::standard;

namespace {

const Real kRef = 440.0;
const Real kMin = 20.0, kMax = 3500.0;

// HPCP::initHarmonicContributionTable, replicated (the table is protected):
// the distinct semitone positions of harmonics 0..n and their strengths
void harmonicTable(int nHarmonics, vector<Real>& semitone, vector<Real>& strength) {
  const Real precision = 0.00001;
  semitone.clear(); strength.clear();
  for (int i = 0; i <= nHarmonics; i++) {
    Real st = 12.0 * log2(i + 1.0);
    Real octweight = max(1.0, (st / 12.0) * 0.5);
    while (st >= 12.0 - precision) st -= 12.0;
    size_t k = 0;
    for (; k < semitone.size(); k++)
      if (semitone[k] > st - precision && semitone[k] < st + precision) break;
    if (k == semitone.size()) { semitone.push_back(st); strength.push_back(1.0 / octweight); }
    else strength[k] += 1.0 / octweight;
  }
}

Algorithm* makeHpcp(const string& which) {
  if (which == "key")
    return AlgorithmFactory::create("HPCP", "size", 36, "referenceFrequency", kRef,
                                    "bandPreset", false, "minFrequency", kMin, "maxFrequency", kMax,
                                    "weightType", "cosine", "nonLinear", false, "windowSize", 1.);
  int size = which == "tuning" ? 120 : 36;
  return AlgorithmFactory::create("HPCP", "size", size, "referenceFrequency", kRef, "harmonics", 8,
                                  "bandPreset", true, "minFrequency", kMin, "maxFrequency", kMax,
                                  "bandSplitFrequency", 500.0, "weightType", "cosine",
                                  "nonLinear", true, "windowSize", 0.5);
}

} // namespace

int main(int argc, char* argv[]) {
  if (argc < 2 || argc > 3) {
    cerr << "usage: " << argv[0] << " <input> [repeats=5]" << endl;
    return 1;
  }
  const int repeats = argc == 3 ? max(1, atoi(argv[2])) : 5;
  essentia::init();
  int rc = 0;
  try {
    // ---- capture the spectral peaks of every frame ----
    vector<Real> audio;
    Algorithm* loader = AlgorithmFactory::create("MonoLoader", "filename", string(argv[1]), "sampleRate", 44100.);
    loader->output("audio").set(audio);
    loader->compute();
    delete loader;

    Algorithm* fc = AlgorithmFactory::create("FrameCutter", "frameSize", 4096, "hopSize", 2048);
    Algorithm* w = AlgorithmFactory::create("Windowing", "type", "blackmanharris62", "zeroPadding", 0);
    Algorithm* spec = AlgorithmFactory::create("Spectrum");
    Algorithm* peaks = AlgorithmFactory::create("SpectralPeaks", "maxPeaks", 60, "magnitudeThreshold", 0.00001,
                                                "minFrequency", 20.0, "maxFrequency", 3500.0, "orderBy", "magnitude");
    vector<Real> frame, wframe, spectrum, f, m;
    fc->input("signal").set(audio);
    fc->output("frame").set(frame);
    w->input("frame").set(frame);
    w->output("frame").set(wframe);
    spec->input("frame").set(wframe);
    spec->output("spectrum").set(spectrum);
    peaks->input("spectrum").set(spectrum);
    peaks->output("frequencies").set(f);
    peaks->output("magnitudes").set(m);
    vector<vector<Real> > freqs, mags;
    for (;;) {
      fc->compute();
      if (frame.empty()) break;
      w->compute(); spec->compute(); peaks->compute();
      freqs.push_back(f); mags.push_back(m);
    }
    delete fc; delete w; delete spec; delete peaks;
    size_t nPeaks = 0, nInRange = 0;
    for (size_t i = 0; i < freqs.size(); i++) {
      nPeaks += freqs[i].size();
      for (size_t j = 0; j < freqs[i].size(); j++) if (freqs[i][j] >= kMin && freqs[i][j] <= kMax) nInRange++;
    }
    printf("input=%s seconds=%.2f frames=%u peaks=%u peaks_in_range=%u repeats=%d\n", argv[1],
           audio.size() / 44100.0, (unsigned)freqs.size(), (unsigned)nPeaks, (unsigned)nInRange, repeats);

    // ---- 1. the real algorithm, per configuration ----
    const char* configs[] = { "key", "chords", "tuning" };
    for (int c = 0; c < 3; c++) {
      Algorithm* h = makeHpcp(configs[c]);
      vector<Real> fi, mi, out;
      h->input("frequencies").set(fi);
      h->input("magnitudes").set(mi);
      h->output("hpcp").set(out);
      double best = 1e30; double check = 0;
      for (int r = 0; r < repeats; r++) {
        double t0 = x6profile::now();
        for (size_t i = 0; i < freqs.size(); i++) {
          fi = freqs[i]; mi = mags[i];   // the copy the streaming wrapper also makes; timed in "copy" below
          h->compute();
          check += out.empty() ? 0 : out[0];
        }
        best = min(best, x6profile::now() - t0);
      }
      delete h;
      printf("hpcp config=%s seconds=%.6f check=%.6g\n", configs[c], best, check);
    }
    {  // the input copy alone, so it can be subtracted from "hpcp"
      vector<Real> fi, mi; double best = 1e30; double sink = 0;
      for (int r = 0; r < repeats; r++) {
        double t0 = x6profile::now();
        for (size_t i = 0; i < freqs.size(); i++) { fi = freqs[i]; mi = mags[i]; sink += fi.size(); }
        best = min(best, x6profile::now() - t0);
      }
      printf("copy seconds=%.6f check=%.6g\n", best, sink);
    }

    // ---- 1b. HPCPFused (x7) against the two HPCPs it replaces: bit for bit on
    //          every frame, then its time against chords + tuning ----
    {
      Algorithm* ch = makeHpcp("chords");
      Algorithm* tu = makeHpcp("tuning");
      Algorithm* fu = AlgorithmFactory::create("HPCPFused", "size", 36, "sizeHighRes", 120,
                                               "referenceFrequency", kRef, "harmonics", 8,
                                               "bandPreset", true, "minFrequency", kMin, "maxFrequency", kMax,
                                               "bandSplitFrequency", 500.0, "weightType", "cosine",
                                               "nonLinear", true, "windowSize", 0.5);
      vector<Real> fi, mi, oc, ot, fa, fb;
      ch->input("frequencies").set(fi); ch->input("magnitudes").set(mi); ch->output("hpcp").set(oc);
      tu->input("frequencies").set(fi); tu->input("magnitudes").set(mi); tu->output("hpcp").set(ot);
      fu->input("frequencies").set(fi); fu->input("magnitudes").set(mi);
      fu->output("hpcp").set(fa); fu->output("hpcpHighRes").set(fb);
      size_t badA = 0, badB = 0;
      for (size_t i = 0; i < freqs.size(); i++) {
        fi = freqs[i]; mi = mags[i];
        ch->compute(); tu->compute(); fu->compute();
        if (fa.size() != oc.size() || (fa.size() && memcmp(&fa[0], &oc[0], fa.size() * sizeof(Real)))) badA++;
        if (fb.size() != ot.size() || (fb.size() && memcmp(&fb[0], &ot[0], fb.size() * sizeof(Real)))) badB++;
      }
      double best = 1e30, check = 0;
      for (int r = 0; r < repeats; r++) {
        double t0 = x6profile::now();
        for (size_t i = 0; i < freqs.size(); i++) {
          fi = freqs[i]; mi = mags[i];
          fu->compute();
          check += fa.empty() ? 0 : fa[0];
        }
        best = min(best, x6profile::now() - t0);
      }
      delete ch; delete tu; delete fu;
      printf("fused seconds=%.6f frames_differing_36=%u frames_differing_120=%u check=%.6g\n",
             best, (unsigned)badA, (unsigned)badB, check);
    }

    // ---- 2. the shared walk alone (what a fused chords+tuning HPCP computes once) ----
    // per in-range peak, per harmonic position: f = freq * pow(2, -st/12), the
    // band choice, and log2(f / ref) - as HPCP::addContribution /
    // addContributionWithWeight compute them, without the bin windows
    {
      vector<Real> st, hs;
      harmonicTable(8, st, hs);
      double best = 1e30; double sink = 0; size_t walks = 0;
      for (int r = 0; r < repeats; r++) {
        walks = 0;
        double t0 = x6profile::now();
        for (size_t i = 0; i < freqs.size(); i++) {
          const vector<Real>& fr = freqs[i];
          const vector<Real>& mg = mags[i];
          for (size_t j = 0; j < fr.size(); j++) {
            Real freq = fr[j];
            if (!(freq >= kMin && freq <= kMax)) continue;
            Real lowBand = freq < 500.0 ? 1 : 0;
            for (size_t k = 0; k < st.size(); k++) {
              Real fh = freq * pow(2., -st[k] / 12.0);
              Real octave = log2(fh / kRef);
              sink += octave * hs[k] + mg[j] * lowBand;
              walks++;
            }
          }
        }
        best = min(best, x6profile::now() - t0);
      }
      printf("walk harmonic_positions=%u contributions=%u seconds=%.6f check=%.6g\n",
             (unsigned)st.size(), (unsigned)walks, best, sink);
    }

    // ---- 3. the per-call band-preset allocation alone (hpcp_LO / hpcp_HI) ----
    const int sizes[] = { 36, 120 };
    for (int s = 0; s < 2; s++) {
      double best = 1e30; double sink = 0;
      for (int r = 0; r < repeats; r++) {
        double t0 = x6profile::now();
        for (size_t i = 0; i < freqs.size(); i++) {
          vector<Real> lo, hi;
          lo.resize(sizes[s]); fill(lo.begin(), lo.end(), (Real)0.0);
          hi.resize(sizes[s]); fill(hi.begin(), hi.end(), (Real)0.0);
          sink += lo[0] + hi[sizes[s] - 1] + (Real)lo.size();
        }
        best = min(best, x6profile::now() - t0);
      }
      printf("alloc size=%d seconds=%.6f check=%.6g\n", sizes[s], best, sink);
    }
  }
  catch (EssentiaException& e) {
    cerr << "ERROR: " << e.what() << endl;
    rc = 2;
  }
  essentia::shutdown();
  return rc;
}
