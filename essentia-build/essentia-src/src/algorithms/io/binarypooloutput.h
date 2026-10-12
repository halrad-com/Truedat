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

#ifndef ESSENTIA_BINARY_POOL_OUTPUT_H
#define ESSENTIA_BINARY_POOL_OUTPUT_H

#include "algorithm.h"
#include "pool.h"

namespace essentia {
namespace standard {

// Writes a Pool to one flat binary file ("ESPB" format, version 1). A sibling
// of YamlOutput, added for the truedat output-x64.5 build so the frame-level
// pool can be handed off as raw little-endian float32 arrays instead of
// indented JSON. YamlOutput is untouched; this is an additional writer.
// The byte layout is specified in essentia-build/FRAMES-BINARY-FORMAT.md.
class BinaryPoolOutput : public Algorithm {

 protected:
  Input<Pool> _pool;
  std::string _filename;
  std::vector<std::string> _include;
  std::string _tag;

  bool isIncluded(const std::string& name) const;

 public:

  BinaryPoolOutput() {
    declareInput(_pool, "pool", "Pool to serialize into a binary ESPB file");
  }

  void declareParameters() {
    declareParameter("filename", "output filename", "", "");
    declareParameter("include", "descriptor name prefixes to write (a prefix matches a descriptor named exactly so or starting with the prefix followed by a dot); empty writes every descriptor", "", std::vector<std::string>());
    declareParameter("tag", "free-text tag stored in the file header (for example the extractor build id); empty writes an empty tag", "", "");
  }

  void compute();
  void configure();

  static const char* name;
  static const char* category;
  static const char* description;

};

} // namespace standard
} // namespace essentia


#endif // ESSENTIA_BINARY_POOL_OUTPUT_H
