/*
 * truedat x7: the shared-decode spool of the AudioLoader (output-x64.7), held
 * in RAM.
 *
 * The music extractor decodes its input four times - the EBU R128 / metadata
 * pass, replay gain, and the two descriptor networks. With a spool, the first
 * loader RECORDS what it pushes and the other three REPLAY it instead of
 * decoding: one record per process() call, holding exactly the interleaved
 * float bytes the loader converted for that call (0 bytes when the call pushed
 * nothing), plus the metadata tokens the loader pushes at reset. A replaying
 * loader hands those bytes to the same copy routine the decoding loader uses,
 * so every downstream graph sees the same tokens in the same chunks, one
 * decoded frame per call, and computes the same result.
 *
 * Memory only - no file is ever opened (operator, 2026-10-25: no repeated SSD
 * writes). A spool has a byte cap: the recording loader skips a track whose
 * estimated size exceeds it, and a record that would take the allocation past
 * it abandons the spool (freed) - the other passes then decode as before. The
 * cap counts the payload blocks and the per-record sizes.
 *
 * The extractor runs one track per process on one thread; spools are found by
 * key in a process-wide registry with no locking. Header-only and free of
 * Essentia and FFmpeg types, so a native ASan test can drive it.
 */

#ifndef ESSENTIA_AUDIOLOADER_SPOOL_H
#define ESSENTIA_AUDIOLOADER_SPOOL_H

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <map>
#include <new>
#include <string>
#include <vector>

namespace essentia {
namespace audioloader_spool {

const uint64_t kBlock = 4u << 20;   // payload is held in fixed blocks: no reallocation copies

class Spool {
 public:
  explicit Spool(uint64_t cap) : cap(cap), channels(0), sampleRate(0), bitRate(0),
                                 _recording(false), _complete(false), _payload(0), _allocated(0), _tail(0) {}

  const uint64_t cap;   // bytes: payload blocks + per-record sizes never exceed it

  // the metadata tokens the recording loader pushed at reset
  uint32_t channels;
  uint32_t sampleRate;
  int32_t bitRate;
  std::string codec;

  bool complete() const { return _complete; }
  uint64_t records() const { return _sizes.size(); }
  uint64_t payload() const { return _payload; }
  uint64_t allocated() const { return _allocated + _sizes.capacity() * sizeof(uint32_t); }

  // Starts a recording, discarding anything held before.
  void begin(uint32_t ch, uint32_t rate, int32_t br, const std::string& name) {
    release();
    channels = ch; sampleRate = rate; bitRate = br; codec = name;
    _recording = true;
  }

  // One record. False when it would take the spool past its cap (or memory
  // runs out): the spool is then abandoned and holds nothing.
  bool append(const void* data, uint32_t bytes) {
    if (!_recording) return false;
    try {
      // room for this record's size entry and its bytes, within the cap; the
      // size table grows by explicit doubling so its footprint is known
      uint64_t sizesCap = _sizes.size() < _sizes.capacity() ? _sizes.capacity()
                        : std::max<uint64_t>(1024, 2 * _sizes.capacity());
      uint64_t free = _blocks.empty() ? 0 : kBlockSize() - _tail;
      uint64_t newBlocks = bytes > free ? (bytes - free + kBlockSize() - 1) / kBlockSize() : 0;
      if (_allocated + newBlocks * kBlockSize() + sizesCap * sizeof(uint32_t) > cap) { abandon(); return false; }

      if (sizesCap != _sizes.capacity()) _sizes.reserve(sizesCap);
      _sizes.push_back(bytes);
      const uint8_t* src = static_cast<const uint8_t*>(data);
      uint32_t left = bytes;
      while (left) {
        if (_blocks.empty() || _tail == kBlockSize()) {
          _blocks.push_back(std::vector<uint8_t>(kBlockSize()));
          _allocated += kBlockSize();
          _tail = 0;
        }
        uint32_t n = (uint32_t)std::min<uint64_t>(left, kBlockSize() - _tail);
        memcpy(&_blocks.back()[_tail], src, n);
        _tail += n; src += n; left -= n;
      }
      _payload += bytes;
      return true;
    }
    catch (const std::bad_alloc&) {
      abandon();
      return false;
    }
  }

  // The recording ended normally: the spool can be replayed.
  void finish() {
    if (_recording) _complete = true;
    _recording = false;
  }

  // Stops recording and frees everything.
  void abandon() { release(); }

  void release() {
    std::vector<std::vector<uint8_t> >().swap(_blocks);
    std::vector<uint32_t>().swap(_sizes);
    _recording = false; _complete = false;
    _payload = 0; _allocated = 0; _tail = 0;
  }

 private:
  // the block size: kBlock, or an eighth of a small cap (at least 4 KiB), so a
  // cap of a few blocks still leaves room for the size table and partial blocks
  uint64_t kBlockSize() const { return cap >= 8 * kBlock ? kBlock : std::max<uint64_t>(4096, cap / 8); }

  friend class Cursor;
  bool _recording;
  bool _complete;
  uint64_t _payload;
  uint64_t _allocated;
  uint64_t _tail;   // bytes used in the last block
  std::vector<uint32_t> _sizes;
  std::vector<std::vector<uint8_t> > _blocks;
};

// Reads a complete spool's records in order.
class Cursor {
 public:
  Cursor() : _s(0), _rec(0), _blk(0), _off(0) {}
  explicit Cursor(const Spool* s) : _s(s), _rec(0), _blk(0), _off(0) {}

  bool active() const { return _s != 0; }

  // The next record's byte count. False at the end.
  bool next(uint32_t& bytes) {
    if (!_s || _rec >= _s->_sizes.size()) return false;
    bytes = _s->_sizes[_rec++];
    return true;
  }

  // The current record's bytes.
  void read(void* dst, uint32_t bytes) {
    uint8_t* out = static_cast<uint8_t*>(dst);
    uint64_t bs = _s->kBlockSize();
    while (bytes) {
      if (_off == bs) { _blk++; _off = 0; }
      uint32_t n = (uint32_t)std::min<uint64_t>(bytes, bs - _off);
      memcpy(out, &_s->_blocks[_blk][_off], n);
      _off += n; out += n; bytes -= n;
    }
  }

 private:
  const Spool* _s;
  size_t _rec;
  size_t _blk;
  uint64_t _off;
};

// ---- the process-wide registry ----

inline std::map<std::string, Spool*>& registry() {
  static std::map<std::string, Spool*> r;
  return r;
}

inline Spool* find(const std::string& key) {
  std::map<std::string, Spool*>::iterator it = registry().find(key);
  return it == registry().end() ? 0 : it->second;
}

inline Spool* create(const std::string& key, uint64_t cap) {
  Spool*& s = registry()[key];
  delete s;
  s = new Spool(cap);
  return s;
}

inline void release(const std::string& key) {
  std::map<std::string, Spool*>::iterator it = registry().find(key);
  if (it == registry().end()) return;
  delete it->second;
  registry().erase(it);
}

// Frees the keyed spool when it goes out of scope - every exit, exceptions included.
class Guard {
 public:
  explicit Guard(const std::string& key) : _key(key) {}
  ~Guard() { if (!_key.empty()) release(_key); }
 private:
  std::string _key;
  Guard(const Guard&);
  Guard& operator=(const Guard&);
};

} // namespace audioloader_spool
} // namespace essentia

#endif // ESSENTIA_AUDIOLOADER_SPOOL_H
