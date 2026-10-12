/*
 * Copyright (C) 2006-2021  Music Technology Group - Universitat Pompeu Fabra
 *
 * This file is part of Essentia
 *
 * Essentia is free software: you can redistribute it and/or modify it under
 * the terms of the GNU Affero General Public License as published by the Free
 * Software Foundation (FSF), either version 3 of the License, or (at your
 * option) any later version.
 *
 * This program is distributed in the hope that it will be useful, but WITHOUT
 * ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
 * FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more
 * details.
 *
 * You should have received a copy of the Affero GNU General Public License
 * version 3 along with this program.  If not, see http://www.gnu.org/licenses/
 */

#include "binarypooloutput.h"
#include "essentia.h"
#include <fstream>
#include <algorithm>
#include <limits>
#include <cstring>
#include <stdint.h>

using namespace std;
using namespace essentia;
using namespace standard;

const char* BinaryPoolOutput::name = "BinaryPoolOutput";
const char* BinaryPoolOutput::category = "Input/output";
const char* BinaryPoolOutput::description = DOC("This algorithm writes a Pool to a flat binary file (ESPB format, version 1).\n"
  "\n"
  "The file is a fixed header, a descriptor table and a payload of raw little-endian "
  "float32 arrays / length-prefixed UTF-8 strings. Each descriptor table entry carries "
  "the descriptor name, its data type, its count and dims, and the absolute offset and "
  "byte length of its payload, so a reader can block-copy any descriptor without parsing "
  "the others. Numeric data is written as float32 (the Pool's Real) with no rounding.\n"
  "\n"
  "Supported Pool contents: single Real, vector<Real> (single or accumulated), "
  "vector<vector<Real>> (rectangular; a ragged matrix is skipped with a warning), single "
  "string and vector<string>. vector<vector<string>>, Array2D, StereoSample and Tensor "
  "entries are skipped with a warning.\n"
  "\n"
  "The 'include' parameter filters by descriptor name prefix using the Pool namespace rule: "
  "a prefix P matches a descriptor named exactly P or one starting with 'P.'. An empty list "
  "writes everything. The full byte layout is documented in FRAMES-BINARY-FORMAT.md next to "
  "the truedat extractor build notes.");

// The format stores Real as IEEE-754 binary32. Fail the build, not the reader,
// if that ever stops being true for this tree.
static_assert(sizeof(Real) == 4, "BinaryPoolOutput: ESPB v1 stores Real as float32; sizeof(Real) must be 4");
static_assert(std::numeric_limits<Real>::is_iec559, "BinaryPoolOutput: Real must be IEEE-754");

namespace {

const uint32_t ESPB_FORMAT_VERSION = 1;
const uint32_t ESPB_BYTE_ORDER_MARK = 0x04030201u; // read back as this value on a little-endian reader

enum DType {
  DT_F32_SCALAR    = 1, // one Real                 count=1,     dims=1
  DT_F32_VECTOR    = 2, // vector<Real>             count=n,     dims=1
  DT_F32_MATRIX    = 3, // vector<vector<Real>>     count=rows,  dims=cols
  DT_STRING        = 4, // one string               count=1,     dims=0
  DT_STRING_VECTOR = 5  // vector<string>           count=n,     dims=0
};

enum Flags {
  FLAG_SINGLE = 1 // value came from Pool::set (a single value), not Pool::add (a per-frame accumulation)
};

struct Entry {
  string name;
  uint32_t dtype;
  uint32_t flags;
  uint32_t count;
  uint32_t dims;
  uint64_t offset;
  uint64_t length;
  // exactly one of these is non-null, selected by dtype
  const Real* scalar;
  const vector<Real>* vec;
  const vector<vector<Real> >* mat;
  const string* str;
  const vector<string>* strs;

  Entry() : dtype(0), flags(0), count(0), dims(0), offset(0), length(0),
            scalar(0), vec(0), mat(0), str(0), strs(0) {}
};

bool byName(const Entry& a, const Entry& b) { return a.name < b.name; }

bool hostIsLittleEndian() {
  uint32_t probe = 1;
  unsigned char first;
  memcpy(&first, &probe, 1);
  return first == 1;
}

void putU32(ostream& out, uint32_t v) {
  unsigned char b[4] = { (unsigned char)(v & 0xFF), (unsigned char)((v >> 8) & 0xFF),
                         (unsigned char)((v >> 16) & 0xFF), (unsigned char)((v >> 24) & 0xFF) };
  out.write((const char*)b, 4);
}

void putU64(ostream& out, uint64_t v) {
  putU32(out, (uint32_t)(v & 0xFFFFFFFFu));
  putU32(out, (uint32_t)(v >> 32));
}

void putString(ostream& out, const string& s) {
  putU32(out, (uint32_t)s.size());
  out.write(s.data(), (streamsize)s.size());
}

void putF32Array(ostream& out, const Real* data, size_t n, bool hostLE) {
  if (n == 0) return;
  if (hostLE) {
    out.write((const char*)data, (streamsize)(n * sizeof(Real)));
    return;
  }
  for (size_t i = 0; i < n; ++i) {
    uint32_t bits;
    memcpy(&bits, &data[i], 4);
    putU32(out, bits);
  }
}

uint64_t stringBytes(const string& s) { return 4 + (uint64_t)s.size(); }

} // anonymous namespace


void BinaryPoolOutput::configure() {
  _filename = parameter("filename").toString();
  _include = parameter("include").toVectorString();
  _tag = parameter("tag").toString();
  if (_filename == "") throw EssentiaException("BinaryPoolOutput: please provide a valid filename");
}


bool BinaryPoolOutput::isIncluded(const string& name) const {
  if (_include.empty()) return true;
  for (size_t i = 0; i < _include.size(); ++i) {
    const string& p = _include[i];
    if (p.empty()) continue;
    if (name == p) return true;
    if (name.size() > p.size() && name.compare(0, p.size(), p) == 0 && name[p.size()] == '.') return true;
  }
  return false;
}


void BinaryPoolOutput::compute() {
  const Pool& p = _pool.get();
  vector<Entry> entries;

  // ---- collect: one Entry per supported descriptor that passes the filter ----

  for (map<string, Real>::const_iterator it = p.getSingleRealPool().begin(); it != p.getSingleRealPool().end(); ++it) {
    if (!isIncluded(it->first)) continue;
    Entry e; e.name = it->first; e.dtype = DT_F32_SCALAR; e.flags = FLAG_SINGLE;
    e.count = 1; e.dims = 1; e.length = 4; e.scalar = &it->second;
    entries.push_back(e);
  }

  for (map<string, vector<Real> >::const_iterator it = p.getRealPool().begin(); it != p.getRealPool().end(); ++it) {
    if (!isIncluded(it->first)) continue;
    Entry e; e.name = it->first; e.dtype = DT_F32_VECTOR; e.flags = 0;
    e.count = (uint32_t)it->second.size(); e.dims = 1; e.length = (uint64_t)it->second.size() * 4; e.vec = &it->second;
    entries.push_back(e);
  }

  for (map<string, vector<Real> >::const_iterator it = p.getSingleVectorRealPool().begin(); it != p.getSingleVectorRealPool().end(); ++it) {
    if (!isIncluded(it->first)) continue;
    Entry e; e.name = it->first; e.dtype = DT_F32_VECTOR; e.flags = FLAG_SINGLE;
    e.count = (uint32_t)it->second.size(); e.dims = 1; e.length = (uint64_t)it->second.size() * 4; e.vec = &it->second;
    entries.push_back(e);
  }

  for (map<string, vector<vector<Real> > >::const_iterator it = p.getVectorRealPool().begin(); it != p.getVectorRealPool().end(); ++it) {
    if (!isIncluded(it->first)) continue;
    const vector<vector<Real> >& m = it->second;
    size_t cols = m.empty() ? 0 : m[0].size();
    bool rectangular = true;
    for (size_t r = 1; r < m.size(); ++r) if (m[r].size() != cols) { rectangular = false; break; }
    if (!rectangular) {
      E_WARNING("BinaryPoolOutput: descriptor '" << it->first << "' is a ragged matrix (rows differ in length); skipped");
      continue;
    }
    Entry e; e.name = it->first; e.dtype = DT_F32_MATRIX; e.flags = 0;
    e.count = (uint32_t)m.size(); e.dims = (uint32_t)cols; e.length = (uint64_t)m.size() * cols * 4; e.mat = &m;
    entries.push_back(e);
  }

  for (map<string, string>::const_iterator it = p.getSingleStringPool().begin(); it != p.getSingleStringPool().end(); ++it) {
    if (!isIncluded(it->first)) continue;
    Entry e; e.name = it->first; e.dtype = DT_STRING; e.flags = FLAG_SINGLE;
    e.count = 1; e.dims = 0; e.length = stringBytes(it->second); e.str = &it->second;
    entries.push_back(e);
  }

  for (map<string, vector<string> >::const_iterator it = p.getStringPool().begin(); it != p.getStringPool().end(); ++it) {
    if (!isIncluded(it->first)) continue;
    Entry e; e.name = it->first; e.dtype = DT_STRING_VECTOR; e.flags = 0;
    e.count = (uint32_t)it->second.size(); e.dims = 0; e.strs = &it->second;
    e.length = 0; for (size_t i = 0; i < it->second.size(); ++i) e.length += stringBytes(it->second[i]);
    entries.push_back(e);
  }

  for (map<string, vector<string> >::const_iterator it = p.getSingleVectorStringPool().begin(); it != p.getSingleVectorStringPool().end(); ++it) {
    if (!isIncluded(it->first)) continue;
    Entry e; e.name = it->first; e.dtype = DT_STRING_VECTOR; e.flags = FLAG_SINGLE;
    e.count = (uint32_t)it->second.size(); e.dims = 0; e.strs = &it->second;
    e.length = 0; for (size_t i = 0; i < it->second.size(); ++i) e.length += stringBytes(it->second[i]);
    entries.push_back(e);
  }

  // Unsupported pool kinds: name them so an absence in the file is explainable.
  for (map<string, vector<vector<string> > >::const_iterator it = p.getVectorStringPool().begin(); it != p.getVectorStringPool().end(); ++it)
    if (isIncluded(it->first)) E_WARNING("BinaryPoolOutput: descriptor '" << it->first << "' is a vector<vector<string>>; not supported by ESPB v1, skipped");
  for (map<string, vector<TNT::Array2D<Real> > >::const_iterator it = p.getArray2DRealPool().begin(); it != p.getArray2DRealPool().end(); ++it)
    if (isIncluded(it->first)) E_WARNING("BinaryPoolOutput: descriptor '" << it->first << "' is an Array2D; not supported by ESPB v1, skipped");
  for (map<string, vector<StereoSample> >::const_iterator it = p.getStereoSamplePool().begin(); it != p.getStereoSamplePool().end(); ++it)
    if (isIncluded(it->first)) E_WARNING("BinaryPoolOutput: descriptor '" << it->first << "' is a StereoSample stream; not supported by ESPB v1, skipped");
  if (p.getSingleTensorRealPool().begin() != p.getSingleTensorRealPool().end() ||
      p.getTensorRealPool().begin() != p.getTensorRealPool().end()) {
    E_WARNING("BinaryPoolOutput: tensors are not supported by ESPB v1; the tensors in this pool are skipped");
  }

  sort(entries.begin(), entries.end(), byName);

  // ---- lay out: header, tag, table, then payloads back to back ----

  uint64_t tableBytes = 0;
  for (size_t i = 0; i < entries.size(); ++i) {
    tableBytes += 4 + entries[i].name.size() // nameLen + name
                + 4 + 4 + 4 + 4               // dtype, flags, count, dims
                + 8 + 8;                      // offset, length
  }
  const uint64_t headerBytes = 4 + 4 + 4 + 4 + 8 + stringBytes(_tag);
  uint64_t cursor = headerBytes + tableBytes;
  for (size_t i = 0; i < entries.size(); ++i) {
    entries[i].offset = cursor;
    cursor += entries[i].length;
  }
  const uint64_t payloadOffset = headerBytes + tableBytes;

  // ---- write ----

  ofstream out(_filename.c_str(), ios::out | ios::binary | ios::trunc);
  if (!out.good()) throw EssentiaException("BinaryPoolOutput: cannot open '", _filename, "' for writing");

  out.write("ESPB", 4);
  putU32(out, ESPB_FORMAT_VERSION);
  putU32(out, ESPB_BYTE_ORDER_MARK);
  putU32(out, (uint32_t)entries.size());
  putU64(out, payloadOffset);
  putString(out, _tag);

  for (size_t i = 0; i < entries.size(); ++i) {
    const Entry& e = entries[i];
    putString(out, e.name);
    putU32(out, e.dtype);
    putU32(out, e.flags);
    putU32(out, e.count);
    putU32(out, e.dims);
    putU64(out, e.offset);
    putU64(out, e.length);
  }

  const bool hostLE = hostIsLittleEndian();
  for (size_t i = 0; i < entries.size(); ++i) {
    const Entry& e = entries[i];
    switch (e.dtype) {
      case DT_F32_SCALAR:
        putF32Array(out, e.scalar, 1, hostLE);
        break;
      case DT_F32_VECTOR:
        putF32Array(out, e.vec->empty() ? 0 : &(*e.vec)[0], e.vec->size(), hostLE);
        break;
      case DT_F32_MATRIX:
        for (size_t r = 0; r < e.mat->size(); ++r) {
          const vector<Real>& row = (*e.mat)[r];
          putF32Array(out, row.empty() ? 0 : &row[0], row.size(), hostLE);
        }
        break;
      case DT_STRING:
        putString(out, *e.str);
        break;
      case DT_STRING_VECTOR:
        for (size_t s = 0; s < e.strs->size(); ++s) putString(out, (*e.strs)[s]);
        break;
    }
  }

  out.flush();
  if (!out.good()) throw EssentiaException("BinaryPoolOutput: error while writing '", _filename, "'");
  out.close();

  const streamoff written = (streamoff)cursor;
  ifstream check(_filename.c_str(), ios::in | ios::binary | ios::ate);
  if (!check.good() || check.tellg() != written) {
    throw EssentiaException("BinaryPoolOutput: '", _filename, "' size does not match the layout that was written");
  }
}
