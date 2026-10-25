/*
 * truedat output-x64.7 B5: the streaming Danceability and DynamicComplexity
 * no longer hold the whole track (they used to store every sample in a pool
 * and run the standard algorithm at the end). This checks that each gives
 * BIT-identical outputs to the standard algorithm on the same signal and
 * configuration, however the scheduler partitions the stream:
 *
 *  - the CONSUMER partition is set on each algorithm's own input sink (the
 *    AccumulatorAlgorithm default is 4,096) and verified: a counting subclass
 *    records the size of every consume() call, which must be the set size
 *    except for one shorter remainder, and must add up to the signal;
 *  - the producer (VectorInput) chunk is varied independently;
 *  - reuse: the network is reset and run again on the same input, which must
 *    give the same values (the reset restores the default 4,096 partition);
 *  - a non-default configuration of both algorithms.
 *
 * Signals: deterministic noise, an amplitude-modulated sine with digital
 * silence at both ends (DynamicComplexity trims it), lengths that are not a
 * multiple of either frame (441 / 8,820 samples at 44.1 kHz), one shorter
 * than a frame; plus, when a file is given, that file decoded by MonoLoader at
 * 44.1 kHz. An EMPTY stream is not compared: the scheduler never fires a
 * consumer of a source that produced nothing.
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
#include "../algorithms/highlevel/danceability.h"
#include "../algorithms/highlevel/dynamiccomplexity.h"

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

// the algorithms under test, recording the size of every consume() call
template <class Base> class Counting : public Base {
 public:
  vector<int> sizes;
  void consume() {
    sizes.push_back((int)((const vector<Real>*)this->_signal.getTokens())->size());
    Base::consume();
  }
};

struct Config {
  string name;
  ParameterMap dance, dyn;   // empty = defaults
};

struct Result { Real danceability; vector<Real> dfa; Real complexity, loudness; };

static Result viaStandard(const vector<Real>& signal, const Config& cfg) {
  Result r;
  standard::Algorithm* d = standard::AlgorithmFactory::create("Danceability");
  d->configure(cfg.dance);
  d->input("signal").set(signal);
  d->output("danceability").set(r.danceability);
  d->output("dfa").set(r.dfa);
  d->compute();
  delete d;
  standard::Algorithm* c = standard::AlgorithmFactory::create("DynamicComplexity");
  c->configure(cfg.dyn);
  c->input("signal").set(signal);
  c->output("dynamicComplexity").set(r.complexity);
  c->output("loudness").set(r.loudness);
  c->compute();
  delete c;
  return r;
}

// what the factory does (name, declared defaults, configure), for a class it does not know
template <class T> static T* make(const char* name, const ParameterMap& params) {
  T* a = new T();
  a->setName(name);
  a->declareParameters();
  Configurable* cfg = a;
  cfg->configure(params);
  return a;
}

// the partition an algorithm saw: every call `chunk` but one shorter remainder, summing to n
static bool partitionIs(const vector<int>& sizes, int chunk, size_t n) {
  size_t total = 0;
  for (size_t i = 0; i < sizes.size(); i++) {
    total += sizes[i];
    bool last = i + 1 == sizes.size();
    if (last ? (sizes[i] < 1 || sizes[i] > chunk) : sizes[i] != chunk) return false;
  }
  return total == n;
}

static Result readPool(const Pool& pool) {
  Result r;
  r.danceability = pool.value<Real>("danceability");   // release size 0: PC stores one value (set)
  r.dfa = pool.value<vector<Real> >("dfa");
  r.complexity = pool.value<Real>("complexity");
  r.loudness = pool.value<Real>("loudness");
  return r;
}

static void compareResult(const Result& a, const Result& b, const string& at) {
  check(same(a.danceability, b.danceability), at + " danceability");
  check(same(a.dfa, b.dfa), at + " dfa");
  check(same(a.complexity, b.complexity), at + " dynamic_complexity");
  check(same(a.loudness, b.loudness), at + " loudness");
}

static void viaStreaming(const vector<Real>& signal, const Config& cfg, int producer, int consumer,
                         const Result& want, const string& at) {
  Pool pool;
  streaming::VectorInput<Real>* in = new streaming::VectorInput<Real>(&signal);
  in->setAcquireSize(producer);
  Counting<streaming::Danceability>* d = make<Counting<streaming::Danceability> >("Danceability", cfg.dance);
  Counting<streaming::DynamicComplexity>* c = make<Counting<streaming::DynamicComplexity> >("DynamicComplexity", cfg.dyn);
  in->output("data") >> d->input("signal");
  in->output("data") >> c->input("signal");
  d->output("danceability") >> PC(pool, "danceability");
  d->output("dfa") >> PC(pool, "dfa");
  c->output("dynamicComplexity") >> PC(pool, "complexity");
  c->output("loudness") >> PC(pool, "loudness");
  // Network::run does not reset the algorithms, so these hold for the first run
  d->input("signal").setAcquireSize(consumer); d->input("signal").setReleaseSize(consumer);
  c->input("signal").setAcquireSize(consumer); c->input("signal").setReleaseSize(consumer);
  scheduler::Network n(in);
  n.run();
  compareResult(want, readPool(pool), at);
  check(partitionIs(d->sizes, consumer, signal.size()) && partitionIs(c->sizes, consumer, signal.size()),
        at + " consumer partition (" + to_string(d->sizes.size()) + " consume() calls)");

  // reuse: reset and run the same network again; the reset restores the 4,096 default partition
  n.reset();
  d->sizes.clear(); c->sizes.clear();
  n.run();
  compareResult(want, readPool(pool), at + " after reset");
  check(partitionIs(d->sizes, 4096, signal.size()) && partitionIs(c->sizes, 4096, signal.size()),
        at + " after reset: default partition");
}   // the network deletes the algorithms

static void compare(const string& name, const vector<Real>& signal, const Config& cfg,
                    const vector<int>& consumers, const vector<int>& producers) {
  Result a = viaStandard(signal, cfg);
  printf("# %s [%s]: %u samples, danceability %.9g, dfa[%u], dynamic_complexity %.9g, loudness %.9g\n",
         name.c_str(), cfg.name.c_str(), (unsigned)signal.size(), a.danceability, (unsigned)a.dfa.size(), a.complexity, a.loudness);
  int whole = max(1, (int)signal.size());
  for (size_t i = 0; i < consumers.size(); i++) {
    for (size_t j = 0; j < producers.size(); j++) {
      int consumer = consumers[i] > 0 ? consumers[i] : whole;
      int producer = producers[j] > 0 ? producers[j] : whole;
      viaStreaming(signal, cfg, producer, consumer, a,
                   name + " [" + cfg.name + "] consumer " + to_string(consumer) + " producer " + to_string(producer));
    }
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

  Config defaults;
  defaults.name = "defaults";

  // an alternate configuration: other tau range and multiplier; another rate and frame length
  Config alt;
  alt.name = "minTau 500 maxTau 5000 x1.2 / frameSize 0.1, both at 22050 Hz";
  alt.dance.add("minTau", Parameter((Real)500));
  alt.dance.add("maxTau", Parameter((Real)5000));
  alt.dance.add("tauMultiplier", Parameter((Real)1.2));
  alt.dance.add("sampleRate", Parameter((Real)22050));
  alt.dyn.add("sampleRate", Parameter((Real)22050));
  alt.dyn.add("frameSize", Parameter((Real)0.1));

  // consumers: one sample; a prime; one 10 ms frame; one 0.2 s frame minus one; the default; past both; whole
  vector<int> consumers;
  consumers.push_back(1);
  consumers.push_back(7);
  consumers.push_back(441);
  consumers.push_back(8819);
  consumers.push_back(4096);
  consumers.push_back(65536);
  consumers.push_back(0);
  vector<int> producers;
  producers.push_back(1000);
  producers.push_back(0);
  compare("noise", noise, defaults, consumers, producers);
  compare("swell", swell, defaults, consumers, producers);
  compare("short", shortSig, defaults, consumers, producers);
  compare("swell", swell, alt, consumers, producers);

  Result e = viaStandard(vector<Real>(), defaults);
  printf("# empty: standard danceability %.9g, dynamic_complexity %.9g, loudness %.9g (not compared, see header)\n",
         e.danceability, e.complexity, e.loudness);

  if (argc > 1) {
    vector<Real> audio;
    standard::Algorithm* loader = standard::AlgorithmFactory::create("MonoLoader", "filename", string(argv[1]), "sampleRate", sr);
    loader->output("audio").set(audio);
    loader->compute();
    delete loader;
    vector<int> fileConsumers;
    fileConsumers.push_back(7);
    fileConsumers.push_back(8819);
    fileConsumers.push_back(4096);
    fileConsumers.push_back(0);
    compare(string("file ") + argv[1], audio, defaults, fileConsumers, vector<int>(1, 4096));
  }

  printf("%s: %d failure(s)\n", failures ? "FAIL" : "PASS", failures);
  essentia::shutdown();
  return failures ? 1 : 0;
}
