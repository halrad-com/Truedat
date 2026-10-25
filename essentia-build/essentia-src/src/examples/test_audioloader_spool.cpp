/*
 * truedat x7: tests of the AudioLoader's in-memory shared-decode spool
 * (src/essentia/utils/audioloader_spool.h). Header-only code, no Essentia or
 * FFmpeg: built with the other truedat tools (MinGW) and natively under ASan
 * (essentia-build/spike-x6/asan/run-spool-asan.sh).
 *
 * Every scenario records a deterministic sequence of records, then replays it
 * and requires the same record sizes and bytes in the same order; the cap
 * scenarios require that the allocation never exceeds the cap and that an
 * abandoned spool holds nothing.
 *
 *   essentia_test_audioloader_spool        exit 0 = every check passed
 */

#include <cstdio>
#include <cstdlib>
#include <vector>
#include "../essentia/utils/audioloader_spool.h"

using namespace essentia::audioloader_spool;

static int failures = 0;

static void check(bool ok, const char* what) {
  printf("%s %s\n", ok ? "PASS" : "FAIL", what);
  if (!ok) failures++;
}

// deterministic bytes for record r, offset i
static uint8_t byteAt(uint32_t r, uint32_t i) { return (uint8_t)((r * 2654435761u + i * 40503u) >> 13); }

static std::vector<uint32_t> sizes(uint32_t n, uint32_t seed, uint32_t maxBytes) {
  std::vector<uint32_t> s;
  uint32_t x = seed;
  for (uint32_t r = 0; r < n; r++) {
    x = x * 1664525u + 1013904223u;
    uint32_t b = (x >> 8) % (maxBytes + 1);
    if (r % 17 == 0) b = 0;   // calls that push nothing are records too
    s.push_back(b);
  }
  return s;
}

// records `rec` into s; true when every append was accepted
static bool record(Spool& s, const std::vector<uint32_t>& rec) {
  s.begin(2, 44100, 320000, "testcodec");
  std::vector<uint8_t> buf;
  for (uint32_t r = 0; r < rec.size(); r++) {
    buf.resize(rec[r]);
    for (uint32_t i = 0; i < rec[r]; i++) buf[i] = byteAt(r, i);
    if (!s.append(buf.empty() ? 0 : &buf[0], rec[r])) return false;
    if (s.allocated() > s.cap) return false;
  }
  s.finish();
  return true;
}

// replays s and compares with `rec`
static bool replayMatches(const Spool& s, const std::vector<uint32_t>& rec) {
  Cursor c(&s);
  std::vector<uint8_t> buf;
  for (uint32_t r = 0; r < rec.size(); r++) {
    uint32_t n = 0;
    if (!c.next(n) || n != rec[r]) return false;
    buf.assign(n, 0);
    if (n) c.read(&buf[0], n);
    for (uint32_t i = 0; i < n; i++) if (buf[i] != byteAt(r, i)) return false;
  }
  uint32_t extra;
  return !c.next(extra);
}

static uint64_t total(const std::vector<uint32_t>& rec) {
  uint64_t t = 0;
  for (size_t i = 0; i < rec.size(); i++) t += rec[i];
  return t;
}

int main() {
  // 1. a typical track: frames of up to 18 KiB (4,608 stereo f32 samples is
  //    36,864 B; keep a mix), many block boundaries crossed
  {
    std::vector<uint32_t> rec = sizes(5000, 7, 40000);
    Spool s(256u << 20);
    bool ok = record(s, rec);
    check(ok && s.complete(), "typical: records accepted, spool complete");
    check(s.records() == rec.size() && s.payload() == total(rec), "typical: record count and payload");
    check(replayMatches(s, rec), "typical: replay = recording, byte for byte, in order");
    check(s.allocated() <= s.cap, "typical: allocation within the cap");
  }
  // 2. records larger than one block (an APE packet is 294,912 samples = 2.3 MB stereo)
  {
    std::vector<uint32_t> rec;
    rec.push_back(0); rec.push_back(9u << 20); rec.push_back(3); rec.push_back(4u << 20); rec.push_back(1);
    Spool s(64u << 20);
    check(record(s, rec) && s.complete(), "large records: accepted");
    check(replayMatches(s, rec), "large records: replay = recording across blocks");
  }
  // 3. cap reached mid-stream: abandoned, nothing held, not replayable
  {
    std::vector<uint32_t> rec = sizes(4000, 11, 40000);
    Spool s(16u << 20);
    bool ok = record(s, rec);
    check(!ok, "over cap: an append is refused");
    check(!s.complete() && s.allocated() == 0 && s.records() == 0, "over cap: abandoned spool holds nothing");
    s.finish();
    check(!s.complete(), "over cap: finish() after abandonment does not make it complete");
  }
  // 4. cap below one block: still records within it, refuses past it
  {
    std::vector<uint32_t> rec = sizes(40, 3, 1000);
    Spool s(64u << 10);
    check(record(s, rec) && s.complete() && s.allocated() <= s.cap, "small cap: fits and stays within it");
    check(replayMatches(s, rec), "small cap: replay = recording");
    std::vector<uint32_t> big(1, 70u << 10);
    Spool t(64u << 10);
    check(!record(t, big) && t.allocated() == 0, "small cap: a record above the cap abandons");
  }
  // 5. two replays of one spool are independent (replay gain retry, then the two networks)
  {
    std::vector<uint32_t> rec = sizes(800, 5, 30000);
    Spool s(64u << 20);
    record(s, rec);
    check(replayMatches(s, rec) && replayMatches(s, rec) && replayMatches(s, rec), "three replays of one spool");
  }
  // 6. begin() after a completed recording starts clean (a loader reset)
  {
    std::vector<uint32_t> a = sizes(300, 1, 20000), b = sizes(200, 2, 20000);
    Spool s(64u << 20);
    record(s, a);
    record(s, b);
    check(replayMatches(s, b) && s.records() == b.size(), "re-record replaces the previous recording");
  }
  // 7. the registry and the guard
  {
    {
      Guard g("k1");
      Spool* s = create("k1", 1u << 20);
      check(s && find("k1") == s, "registry: create / find");
      create("k1", 2u << 20);
      check(find("k1") && find("k1")->cap == (2u << 20), "registry: create replaces");
      check(find("k2") == 0, "registry: unknown key is absent");
    }
    check(find("k1") == 0, "guard: released at scope exit");
    release("never-created");   // no-op
    check(true, "release of an unknown key is a no-op");
  }

  printf("%d failure(s)\n", failures);
  return failures ? 1 : 0;
}
