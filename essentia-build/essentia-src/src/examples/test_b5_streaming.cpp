/*
 * truedat output-x64.7 B5: the streaming Danceability and DynamicComplexity
 * no longer hold the whole track (they used to store every sample in a pool
 * and run the standard algorithm at the end). This checks that each gives
 * BIT-identical outputs to the standard algorithm on the same signal, for
 * every chunking the scheduler could hand it: the same samples, the same
 * order, the standard algorithm's own arithmetic.
 *
 * Signals: deterministic noise, an amplitude-modulated sine with digital
 * silence at both ends (DynamicComplexity trims it), lengths that are not a
 * multiple of either frame, one shorter than a frame, and empty; plus, when a
 * file is given, that file decoded by MonoLoader at 44.1 kHz.
 *
 *   essentia_test_b5_streaming [<audio file>]     exit 0 = every check passed
 */

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include <essentia/algorithmfactory.h>
#include <essentia/pool.h>
#include <essentia/scheduler/network.h>
#include <essentia/streaming/algorithms/poolstorage.h>
#include <essentia/streaming/algorithms/vectorinput.h>

using namespace std;
using namespace essentia;

static int failures = 0;
static const double kPi = 3.14159265358979323846;   // M_PI is not defined under -std=c++11 on MinGW

static void check(bool ok, const string& what) {
  printf("%s %s\n", ok ? "PASS" : "FAIL", what.c_str());
  if (!ok) failures++;
}

static bool same(Real a, Real b) { return memcmp(&a, &b, sizeof(Real)) == 0; }

static bool same(const vector<Real>& a, const vector<Real>& b) {
  return a.size() == b.size() && (a.empty() || memcmp(&a[0], &b[0], a.size() * sizeof(Real)) == 0);
}

struct Result { Real danceability; vector<Real> dfa; Real complexity, loudness; };

static Result viaStandard(const vector<Real>& signal) {
  Result r;
  standard::Algorithm* d = standard::AlgorithmFactory::create("Danceability");
  d->input("signal").set(signal);
  d->output("danceability").set(r.danceability);
  d->output("dfa").set(r.dfa);
  d->compute();
  delete d;
  standard::Algorithm* c = standard::AlgorithmFactory::create("DynamicComplexity", "sampleRate", 44100.);
  c->input("signal").set(signal);
  c->output("dynamicComplexity").set(r.complexity);
  c->output("loudness").set(r.loudness);
  c->compute();
  delete c;
  return r;
}

static Result viaStreaming(const vector<Real>& signal, int chunk) {
  Pool pool;
  streaming::VectorInput<Real>* in = new streaming::VectorInput<Real>(&signal);
  in->setAcquireSize(chunk);
  streaming::Algorithm* d = streaming::AlgorithmFactory::create("Danceability");
  streaming::Algorithm* c = streaming::AlgorithmFactory::create("DynamicComplexity", "sampleRate", 44100.);
  in->output("data") >> d->input("signal");
  in->output("data") >> c->input("signal");
  d->output("danceability") >> PC(pool, "danceability");
  d->output("dfa") >> PC(pool, "dfa");
  c->output("dynamicComplexity") >> PC(pool, "complexity");
  c->output("loudness") >> PC(pool, "loudness");
  scheduler::Network n(in);
  n.run();
  Result r;
  r.danceability = pool.value<Real>("danceability");   // release size 0: PC stores one value (set)
  r.dfa = pool.value<vector<Real> >("dfa");
  r.complexity = pool.value<Real>("complexity");
  r.loudness = pool.value<Real>("loudness");
  return r;   // the network deletes the algorithms
}

static void compare(const string& name, const vector<Real>& signal, const vector<int>& chunks) {
  Result a = viaStandard(signal);
  printf("# %s: %u samples, danceability %.9g, dfa[%u], dynamic_complexity %.9g, loudness %.9g\n",
         name.c_str(), (unsigned)signal.size(), a.danceability, (unsigned)a.dfa.size(), a.complexity, a.loudness);
  for (size_t i = 0; i < chunks.size(); i++) {
    int chunk = chunks[i] > 0 ? chunks[i] : max(1, (int)signal.size());
    Result b = viaStreaming(signal, chunk);
    string at = name + " chunk " + to_string(chunk);
    check(same(a.danceability, b.danceability), at + " danceability");
    check(same(a.dfa, b.dfa), at + " dfa");
    check(same(a.complexity, b.complexity), at + " dynamic_complexity");
    check(same(a.loudness, b.loudness), at + " loudness");
  }
}

int main(int argc, char** argv) {
  essentia::init();
  const Real sr = 44100;

  // deterministic noise, 30 s + 123 samples (neither 441 nor 8820 divides it)
  vector<Real> noise(30 * 44100 + 123);
  uint32_t x = 12345;
  for (size_t i = 0; i < noise.size(); i++) {
    x = x * 1664525u + 1013904223u;
    noise[i] = ((Real)(x >> 8) / (Real)(1 << 24) - 0.5f) * 0.5f;
  }

  // 2 s of digital silence, 25 s of a 220 Hz sine under a 0.5 Hz swell, 3 s of silence, + 77 samples
  vector<Real> swell(30 * 44100 + 77, 0.0f);
  for (size_t i = 2 * 44100; i < 27 * 44100; i++) {
    Real t = (Real)i / sr;
    swell[i] = (Real)(0.6 * (0.55 + 0.45 * sin(2 * kPi * 0.5 * t)) * sin(2 * kPi * 220.0 * t));
  }

  vector<Real> shortSig(300);   // shorter than one 10 ms frame and one 0.2 s frame
  for (size_t i = 0; i < shortSig.size(); i++) shortSig[i] = (Real)sin(0.05 * i);

  vector<Real> empty;

  vector<int> chunks;
  chunks.push_back(7);
  chunks.push_back(441);
  chunks.push_back(4096);
  chunks.push_back(65536);
  chunks.push_back(0);   // the whole signal in one chunk
  compare("noise", noise, chunks);
  compare("swell", swell, chunks);
  vector<int> small(chunks);
  small.push_back(1);
  compare("short", shortSig, small);
  // an EMPTY stream is not compared: the scheduler never fires a consumer of a source that
  // produced nothing, so neither the streaming form nor the pool-based composite it replaced
  // outputs a value (inferred for the old composite, not run). The standard form's answer:
  Result e = viaStandard(empty);
  printf("# empty: standard danceability %.9g, dynamic_complexity %.9g, loudness %.9g\n", e.danceability, e.complexity, e.loudness);

  if (argc > 1) {
    vector<Real> audio;
    standard::Algorithm* loader = standard::AlgorithmFactory::create("MonoLoader", "filename", string(argv[1]), "sampleRate", sr);
    loader->output("audio").set(audio);
    loader->compute();
    delete loader;
    compare(string("file ") + argv[1], audio, chunks);
  }

  printf("%s: %d failure(s)\n", failures ? "FAIL" : "PASS", failures);
  essentia::shutdown();
  return failures ? 1 : 0;
}
