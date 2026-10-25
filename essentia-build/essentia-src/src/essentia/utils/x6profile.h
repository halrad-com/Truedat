/*
 * truedat x6: opt-in per-algorithm timing for the music extractor.
 *
 * OFF unless the environment variable ESSENTIA_PROFILE names a file; then the
 * scheduler times every streaming process() call (attributed to the current
 * phase and the algorithm's name), the extractor times its standard-mode
 * steps, and report() appends one table per run to that file:
 *
 *   # x6profile <label> wall=<s> cpu=<s>
 *   phase<TAB>algorithm<TAB>instances<TAB>calls<TAB>seconds
 *
 * The extractor is single-threaded, so the per-call wall time is its CPU time
 * while nothing else competes for the core. When off, the scheduler pays one
 * predictable branch per process() call. Never changes any output.
 */

#ifndef ESSENTIA_X6PROFILE_H
#define ESSENTIA_X6PROFILE_H

#include <string>

namespace essentia {
namespace streaming { class Algorithm; }
namespace x6profile {

// true when ESSENTIA_PROFILE names an output file (read once)
bool enabled();

// monotonic high-resolution clock, seconds
double now();

// what runs next is attributed to this phase
void setPhase(const std::string& phase);

// one streaming process() call of `instance` (an algorithm named `name`)
void addCall(const void* instance, const std::string& name, double seconds);

// the same, with the row named after the algorithm plus the parameters that
// tell its instances apart when it declares them - "method", "size",
// "profileType" - e.g. OnsetDetection[method=hfc] vs OnsetDetection[method=complex],
// HPCP[size=36] vs HPCP[size=120]. The name is resolved once per instance and
// phase, so the per-call cost is one hash lookup.
void addCall(const streaming::Algorithm* algo, double seconds);

// a standard-mode step, timed by Scope
void addStep(const std::string& step, double seconds);

// appends the table for this run to the ESSENTIA_PROFILE file
void report(const std::string& label);

// times its own lifetime as a step of the current phase
class Scope {
 public:
  explicit Scope(const char* step) : _step(step), _t0(enabled() ? now() : 0) {}
  ~Scope() { if (enabled()) addStep(_step, now() - _t0); }
 private:
  const char* _step;
  double _t0;
};

} // namespace x6profile
} // namespace essentia

#endif // ESSENTIA_X6PROFILE_H
