/*
 * truedat x6 synthetic decoder-state fixtures (not part of any shipped binary).
 *
 * Drives the AudioLoader's decode step (essentia/utils/audioloader_decode.h,
 * the exact code the loader runs, one frame per step) with a scripted fake
 * decoder, to reach decoder states a corpus may never trigger: several frames
 * per packet, send backpressure (EAGAIN) from a decoder whose output completes
 * asynchronously, several delayed frames released only at drain, a decode
 * error mid-stream, and an API-violating decoder that must not make the loop
 * spin.
 *
 * Every scenario checks the EXACT frame sequence delivered (order, no loss, no
 * duplicates), that the drain packet was sent once, and that no step emitted
 * more than one frame.
 *
 *   essentia_test_audioloader_decodeloop      -> one line per scenario, exit 0 iff all pass
 */

#include <cstdio>
#include <deque>
#include <string>
#include <vector>
#include "../essentia/utils/audioloader_decode.h"

using namespace essentia::streaming::audioloader_detail;

namespace {

struct Frame { int packet; int index; bool operator==(const Frame& o) const { return packet == o.packet && index == o.index; } };

// framesPerPacket: frames each accepted packet produces.
// delay:           frames the decoder holds back; released only by the drain packet.
// lag:             API calls before a produced frame becomes receivable (0 = at once).
//                  While any frame is receivable, send() refuses with EAGAIN, as the
//                  API allows ("user must read output with avcodec_receive_frame()").
// errorPacket:     the packet whose first frame fails with AVERROR_INVALIDDATA (-1 = none).
// violate:         send() and receive() both answer EAGAIN forever (an API violation).
struct FakeDecoder {
  int framesPerPacket = 1, delay = 0, lag = 0, errorPacket = -1;
  bool violate = false;

  long calls = 0;
  int drainSends = 0;
  bool draining = false;
  struct Pending { Frame f; long readyAt; bool error; };
  std::deque<Pending> held, queued;
  Frame current = {-1, -1};

  void tick() { ++calls; }
  bool receivable() const { return !queued.empty() && queued.front().readyAt <= calls; }

  int send(int packet) {
    tick();
    if (violate) return AVERROR(EAGAIN);
    if (draining) return AVERROR_EOF;
    if (receivable()) return AVERROR(EAGAIN);
    for (int i = 0; i < framesPerPacket; ++i)
      held.push_back({{packet, i}, calls + lag, packet == errorPacket && i == 0});
    while ((int)held.size() > delay) { queued.push_back(held.front()); held.pop_front(); }
    return 0;
  }

  int sendDrain() {
    tick();
    ++drainSends;
    if (draining) return AVERROR_EOF;     // a second drain send is refused
    draining = true;
    while (!held.empty()) { queued.push_back(held.front()); held.pop_front(); }
    for (auto& p : queued) p.readyAt = 0; // the drain makes everything receivable
    return 0;
  }

  int receive() {
    tick();
    if (violate) return AVERROR(EAGAIN);
    if (receivable()) {
      Pending p = queued.front(); queued.pop_front();
      if (p.error) return AVERROR_INVALIDDATA;
      current = p.f;
      return 0;
    }
    if (draining && queued.empty()) return AVERROR_EOF;
    return AVERROR(EAGAIN);
  }
};

struct Result { std::vector<Frame> frames; int errors = 0; int steps = 0; int maxFramesPerStep = 0; bool finished = false; };

// The loader's call pattern: step() until STEP_FINISHED, counting frames per step;
// a packet source of `packets` packets then EOF.
Result run(FakeDecoder& d, int packets) {
  Result res;
  DecodeState st;
  int next = 0, current = -1;
  auto readPacket = [&]() -> int { if (next >= packets) return AVERROR_EOF; current = next++; return 0; };
  auto send = [&] { return d.send(current); };
  auto sendDrain = [&] { return d.sendDrain(); };
  auto receive = [&] { return d.receive(); };
  int framesThisStep = 0;
  auto onFrame = [&] { res.frames.push_back(d.current); ++framesThisStep; };
  for (;;) {
    framesThisStep = 0;
    int r = step(st, readPacket, send, sendDrain, receive, onFrame);
    ++res.steps;
    if (framesThisStep > res.maxFramesPerStep) res.maxFramesPerStep = framesThisStep;
    if (r < 0) ++res.errors;
    if (r == STEP_FINISHED) { res.finished = true; break; }
    if (res.steps > 100000) break;     // a spinning loop fails the scenario instead of hanging
  }
  return res;
}

std::vector<Frame> expected(int packets, int framesPerPacket, int skipPacket = -1) {
  std::vector<Frame> v;
  for (int p = 0; p < packets; ++p)
    for (int i = 0; i < framesPerPacket; ++i)
      if (!(p == skipPacket && i == 0)) v.push_back({p, i});
  return v;
}

int failures = 0;

void check(const char* name, bool ok, const std::string& detail) {
  printf("%s %-34s %s\n", ok ? "PASS" : "FAIL", name, detail.c_str());
  if (!ok) ++failures;
}

std::string counts(const Result& r, size_t want, const FakeDecoder& d) {
  char buf[200];
  snprintf(buf, sizeof buf, "frames %u/%u, errors %d, drain sends %d, steps %d, max frames/step %d, api calls %ld%s",
           (unsigned)r.frames.size(), (unsigned)want, r.errors, d.drainSends, r.steps, r.maxFramesPerStep,
           d.calls, r.finished ? "" : ", NOT FINISHED");
  return buf;
}

bool shape(const Result& r, const FakeDecoder& d) {   // invariants every scenario must hold
  return r.finished && d.drainSends == 1 && r.maxFramesPerStep <= 1;
}

} // namespace

int main() {
  const int N = 50;

  { // control: one frame per packet, nothing held back
    FakeDecoder d; Result r = run(d, N); auto want = expected(N, 1);
    check("one-frame-per-packet", r.frames == want && r.errors == 0 && shape(r, d), counts(r, want.size(), d));
  }
  { // a packet that decodes to several frames (APE, WMA Lossless, WMA Pro)
    FakeDecoder d; d.framesPerPacket = 3; Result r = run(d, N); auto want = expected(N, 3);
    check("three-frames-per-packet", r.frames == want && r.errors == 0 && shape(r, d), counts(r, want.size(), d));
  }
  { // one 64-frame packet, as APE packs 294,912 samples in 4,608-sample frames
    FakeDecoder d; d.framesPerPacket = 64; Result r = run(d, 3); auto want = expected(3, 64);
    check("sixty-four-frames-per-packet", r.frames == want && r.errors == 0 && shape(r, d), counts(r, want.size(), d));
  }
  { // asynchronous completion: send() answers EAGAIN while output is pending
    FakeDecoder d; d.lag = 2; Result r = run(d, N); auto want = expected(N, 1);
    check("send-backpressure-eagain", r.frames == want && r.errors == 0 && shape(r, d), counts(r, want.size(), d));
  }
  { // async completion and several frames per packet together
    FakeDecoder d; d.lag = 2; d.framesPerPacket = 2; Result r = run(d, N); auto want = expected(N, 2);
    check("eagain-with-multiframe-packets", r.frames == want && r.errors == 0 && shape(r, d), counts(r, want.size(), d));
  }
  { // several delayed frames released only at drain (AV_CODEC_CAP_DELAY decoders)
    FakeDecoder d; d.delay = 4; Result r = run(d, N); auto want = expected(N, 1);
    check("four-delayed-frames-at-drain", r.frames == want && r.errors == 0 && shape(r, d), counts(r, want.size(), d));
  }
  { // a decode error loses that frame only; every later packet still decodes
    FakeDecoder d; d.errorPacket = 7; Result r = run(d, N); auto want = expected(N, 1, 7);
    check("decode-error-mid-stream", r.frames == want && r.errors == 1 && shape(r, d), counts(r, want.size(), d));
  }
  { // EAGAIN from both send and receive breaks the API; the loop must not spin
    FakeDecoder d; d.violate = true; Result r = run(d, 3);
    bool bounded = r.finished && d.calls < 100;
    check("api-violation-does-not-spin", bounded && r.frames.empty(), counts(r, 0, d));
  }

  printf("%s: %d failure(s)\n", failures ? "FAILED" : "ALL PASS", failures);
  return failures ? 1 : 0;
}
