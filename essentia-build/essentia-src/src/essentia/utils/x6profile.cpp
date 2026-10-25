/*
 * truedat x6: opt-in per-algorithm timing for the music extractor. See
 * x6profile.h.
 */

#include "x6profile.h"
#include "../streaming/streamingalgorithm.h"

#include <cstdio>
#include <cstdlib>
#include <map>
#include <set>
#include <unordered_map>
#include <utility>

#ifdef _WIN32
#include <windows.h>
#include <psapi.h>   // PROCESS_MEMORY_COUNTERS only; the function is fetched from kernel32 at run time
#else
#include <sys/resource.h>
#include <time.h>
#endif

using namespace std;

namespace essentia {
namespace x6profile {

namespace {

struct Entry {
  double seconds;
  long calls;
  set<const void*> instances;
  Entry() : seconds(0), calls(0) {}
};

// (phase, algorithm or step) -> totals; reported sorted by phase, then name
map<pair<string, string>, Entry>& table() {
  static map<pair<string, string>, Entry> t;
  return t;
}

string& phase() {
  static string p = "startup";
  return p;
}

const char* outputPath() {
  static const char* path = getenv("ESSENTIA_PROFILE");
  return (path && *path) ? path : 0;
}

double startTime() {
  static double t0 = now();
  return t0;
}

double processCpuSeconds() {
#ifdef _WIN32
  FILETIME created, exited, kernel, user;
  if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user)) return -1;
  ULARGE_INTEGER k, u;
  k.LowPart = kernel.dwLowDateTime; k.HighPart = kernel.dwHighDateTime;
  u.LowPart = user.dwLowDateTime;   u.HighPart = user.dwHighDateTime;
  return (double)(k.QuadPart + u.QuadPart) * 1e-7;   // 100 ns units
#else
  struct rusage r;
  if (getrusage(RUSAGE_SELF, &r) != 0) return -1;
  return r.ru_utime.tv_sec + r.ru_stime.tv_sec + 1e-6 * (r.ru_utime.tv_usec + r.ru_stime.tv_usec);
#endif
}

// Peak working set of the process in MiB (-1 if unavailable); *current gets the
// working set now. On Windows via K32GetProcessMemoryInfo looked up in kernel32
// so the build needs no psapi import library.
double peakWorkingSetMiB(double* current) {
#ifdef _WIN32
  typedef BOOL (WINAPI *GetPmiFn)(HANDLE, PPROCESS_MEMORY_COUNTERS, DWORD);
  static GetPmiFn fn = (GetPmiFn)GetProcAddress(GetModuleHandleA("kernel32.dll"), "K32GetProcessMemoryInfo");
  if (!fn) return -1;
  PROCESS_MEMORY_COUNTERS pmc;
  pmc.cb = sizeof(pmc);
  if (!fn(GetCurrentProcess(), &pmc, sizeof(pmc))) return -1;
  if (current) *current = (double)pmc.WorkingSetSize / 1048576.0;
  return (double)pmc.PeakWorkingSetSize / 1048576.0;
#else
  struct rusage r;
  if (getrusage(RUSAGE_SELF, &r) != 0) return -1;
  if (current) *current = -1;
  return r.ru_maxrss / 1024.0;   // KiB on Linux
#endif
}

// Bytes the process has written through the I/O manager so far (files, pipes,
// devices), -1 if unavailable - the x7 check that the shared-decode spool adds
// no writes: equal with and without it.
double writtenMiB() {
#ifdef _WIN32
  IO_COUNTERS io;
  if (!GetProcessIoCounters(GetCurrentProcess(), &io)) return -1;
  return (double)io.WriteTransferCount / 1048576.0;
#else
  return -1;
#endif
}

} // namespace

bool enabled() {
  static const bool on = outputPath() != 0;
  if (on) startTime();   // anchor the wall clock at the first query
  return on;
}

double now() {
#ifdef _WIN32
  static LARGE_INTEGER freq = { { 0, 0 } };
  if (freq.QuadPart == 0) QueryPerformanceFrequency(&freq);
  LARGE_INTEGER c;
  QueryPerformanceCounter(&c);
  return (double)c.QuadPart / (double)freq.QuadPart;
#else
  struct timespec ts;
  clock_gettime(CLOCK_MONOTONIC, &ts);
  return ts.tv_sec + 1e-9 * ts.tv_nsec;
#endif
}

void setPhase(const string& p) {
  if (enabled()) phase() = p;
}

void addCall(const void* instance, const string& name, double seconds) {
  Entry& e = table()[make_pair(phase(), name)];
  e.seconds += seconds;
  e.calls += 1;
  e.instances.insert(instance);
}

namespace {

// per-instance row cache: an algorithm's row is found by pointer after the
// first call; (re)resolved when the phase changed, since an address freed in
// one phase can be reused by another algorithm in the next
struct Cached { string phase; Entry* entry; };
unordered_map<const void*, Cached>& cache() {
  static unordered_map<const void*, Cached> c;
  return c;
}

string displayName(const streaming::Algorithm* algo) {
  string n = algo->name();
  static const char* const keys[] = { "method", "size", "profileType" };
  const ParameterMap& declared = algo->defaultParameters();
  for (int i = 0; i < 3; ++i) {
    if (declared.find(keys[i]) == declared.end()) continue;
    const Parameter& p = algo->parameter(keys[i]);
    if (!p.isConfigured()) continue;
    n += (n.find('[') == string::npos ? "[" : ",");
    n += string(keys[i]) + "=" + p.toString();
  }
  if (n.find('[') != string::npos) n += "]";
  return n;
}

} // namespace

void addCall(const streaming::Algorithm* algo, double seconds) {
  Cached& c = cache()[algo];
  if (!c.entry || c.phase != phase()) {
    c.phase = phase();
    c.entry = &table()[make_pair(phase(), displayName(algo))];
    c.entry->instances.insert(algo);
  }
  c.entry->seconds += seconds;
  c.entry->calls += 1;
}

void addStep(const string& step, double seconds) {
  Entry& e = table()[make_pair(phase(), "[step] " + step)];
  e.seconds += seconds;
  e.calls += 1;
}

void report(const string& label) {
  if (!enabled()) return;
  FILE* f = fopen(outputPath(), "a");
  if (!f) {
    fprintf(stderr, "x6profile: cannot append to %s\n", outputPath());
    return;
  }
  double ws = -1;
  double peak = peakWorkingSetMiB(&ws);
  // written_mib: everything the process wrote before this report - the output
  // JSON and frames file included, since report() runs after them
  fprintf(f, "# x6profile %s wall=%.6f cpu=%.6f peakws_mib=%.3f ws_mib=%.3f written_mib=%.6f\n", label.c_str(),
          now() - startTime(), processCpuSeconds(), peak, ws, writtenMiB());
  fprintf(f, "phase\talgorithm\tinstances\tcalls\tseconds\n");
  for (map<pair<string, string>, Entry>::const_iterator it = table().begin(); it != table().end(); ++it) {
    fprintf(f, "%s\t%s\t%u\t%ld\t%.6f\n", it->first.first.c_str(), it->first.second.c_str(),
            (unsigned)it->second.instances.size(), it->second.calls, it->second.seconds);
  }
  fclose(f);
}

} // namespace x6profile
} // namespace essentia
