/*
 * truedat x6 synthetic decoder-state fixtures (not part of any shipped binary).
 *
 * Drives the AudioLoader's send/receive loop (essentia/utils/audioloader_decode.h,
 * the exact code the loader runs) with a scripted fake decoder, to reach decoder
 * states a corpus may never trigger: several frames per packet, send
 * backpressure (EAGAIN) from a decoder whose output completes asynchronously,
 * several delayed frames released only at drain, a decode error mid-stream, and
 * an API-violating decoder that must not make the loop spin.
 *
 * Every scenario checks the EXACT frame sequence delivered (order, no loss, no
 * duplicates), not a count.
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

struct Result { std::vector<Frame> frames; std::vector<int> packetErrors; int drainResult = 0; };

// The loader's call pattern: decodePacket per demuxed packet, then drain at EOF.
Result run(FakeDecoder& d, int packets) {
  Result res;
  auto receive = [&] { return d.receive(); };
  auto onFrame = [&] { res.frames.push_back(d.current); };
  for (int p = 0; p < packets; ++p) {
    int r = decodePacket([&] { return d.send(p); }, receive, onFrame);
    if (r < 0) res.packetErrors.push_back(p);
  }
  res.drainResult = drain([&] { return d.sendDrain(); }, receive, onFrame);
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
  char buf[160];
  snprintf(buf, sizeof buf, "frames %u/%u, packet errors %u, drain sends %d, api calls %ld",
           (unsigned)r.frames.size(), (unsigned)want, (unsigned)r.packetErrors.size(),
           d.drainSends, d.calls);
  return buf;
}

} // namespace

int main() {
  const int N = 50;

  { // control: one frame per packet, nothing held back
    FakeDecoder d; Result r = run(d, N); auto want = expected(N, 1);
    check("one-frame-per-packet", r.frames == want && r.packetErrors.empty(), counts(r, want.size(), d));
  }
  { // a packet that decodes to several frames (WMA, APE, some PCM demuxers)
    FakeDecoder d; d.framesPerPacket = 3; Result r = run(d, N); auto want = expected(N, 3);
    check("three-frames-per-packet", r.frames == want && r.packetErrors.empty(), counts(r, want.size(), d));
  }
  { // asynchronous completion: send() answers EAGAIN while output is pending
    FakeDecoder d; d.lag = 2; Result r = run(d, N); auto want = expected(N, 1);
    check("send-backpressure-eagain", r.frames == want && r.packetErrors.empty(), counts(r, want.size(), d));
  }
  { // async completion and several frames per packet together
    FakeDecoder d; d.lag = 2; d.framesPerPacket = 2; Result r = run(d, N); auto want = expected(N, 2);
    check("eagain-with-multiframe-packets", r.frames == want && r.packetErrors.empty(), counts(r, want.size(), d));
  }
  { // several delayed frames released only at drain (AV_CODEC_CAP_DELAY decoders)
    FakeDecoder d; d.delay = 4; Result r = run(d, N); auto want = expected(N, 1);
    check("four-delayed-frames-at-drain", r.frames == want && r.packetErrors.empty(), counts(r, want.size(), d));
  }
  { // the drain packet is sent exactly once
    FakeDecoder d; d.delay = 4; run(d, N);
    check("drain-sent-once", d.drainSends == 1, "drain sends " + std::to_string(d.drainSends));
  }
  { // a decode error loses that frame only; every later packet still decodes
    FakeDecoder d; d.errorPacket = 7; Result r = run(d, N); auto want = expected(N, 1, 7);
    bool errorReported = r.packetErrors.size() == 1 && r.packetErrors[0] == 7;
    check("decode-error-mid-stream", r.frames == want && errorReported, counts(r, want.size(), d));
  }
  { // EAGAIN from both send and receive breaks the API; the loop must not spin
    FakeDecoder d; d.violate = true; Result r = run(d, 3);
    bool bounded = d.calls < 100;
    check("api-violation-does-not-spin", bounded && r.frames.empty(), counts(r, 0, d));
  }

  printf("%s: %d failure(s)\n", failures ? "FAILED" : "ALL PASS", failures);
  return failures ? 1 : 0;
}
