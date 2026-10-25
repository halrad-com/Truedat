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

#ifndef ESSENTIA_DYNAMICCOMPLEXITY_H
#define ESSENTIA_DYNAMICCOMPLEXITY_H

#include "algorithm.h"
#include <numeric>

namespace essentia {
namespace standard {

class DynamicComplexity : public Algorithm {

 private:
  Input<std::vector<Real> > _signal;
  Output<Real> _complexity;
  Output<Real> _loudness;

  int _frameSize;
  Real _sampleRate;

 public:
  DynamicComplexity() {
    declareInput(_signal, "signal", "the input audio signal");
    declareOutput(_complexity, "dynamicComplexity", "the dynamic complexity coefficient");
    declareOutput(_loudness, "loudness", "an estimate of the loudness [dB]");
  }

  void declareParameters() {
    declareParameter("sampleRate", "the sampling rate of the audio signal [Hz]", "(0,inf)", 44100.);
    declareParameter("frameSize", "the frame size [s]", "(0,inf)", 0.2);
  }


  void configure();
  void compute();

  static const char* name;
  static const char* category;
  static const char* description;

  // The steps of compute(), shared with the streaming form (output-x64.7 B5), which runs them
  // sample by sample and frame by frame instead of holding the whole track: the frame length,
  // the smearing weights, one step of the B-curve filter, one frame's smeared energy, and
  // everything after the per-frame dB series (VdB is consumed in place).
  int frameSize() const { return _frameSize; }
  void smearing(std::vector<Real>& weight, Real& c, Real& Vweight) const;
  static inline Real filterFirst(Real x) { return kB0 * x; }
  static inline Real filterStep(Real x, Real xPrev, Real yPrev) { return kB0*x + kB1*xPrev - kA1*yPrev; }
  static inline Real nextLevel(Real Vms, Real Vweight, Real c, const std::vector<Real>& weight,
                               std::vector<Real>::const_iterator energy) {
    return Vweight*Vms + (1-c)*std::inner_product(weight.begin(), weight.end(), energy, 0.0);
  }
  void fromLevels(std::vector<Real>& VdB, Real& complexity, Real& loudness) const;

 protected:
  // the cheap B-curve's coefficients: nominator { b0, b1 }, denominator { 1, a1 }
  static const Real kB0, kB1, kA1;
  void filter(std::vector<Real>& result, const std::vector<Real>& input) const;
};

} // namespace standard
} // namespace essentia

#include "accumulatoralgorithm.h"

namespace essentia {
namespace streaming {

// Streams the signal (output-x64.7 B5): it keeps the filter's previous input and output, the
// frame being filled and one dB level per completed frame, not the whole track - it used to
// store every sample in a pool and run the standard algorithm at the end. Same values: the
// standard algorithm's own steps do the arithmetic, over the same samples in the same order.
class DynamicComplexity : public AccumulatorAlgorithm {

 protected:
  Sink<Real> _signal;
  Source<Real> _complexity;
  Source<Real> _loudness;

  standard::DynamicComplexity* _dynAlgo;
  int _frameSize;
  std::vector<Real> _weight;
  Real _c, _Vweight;
  long long _n;             // samples consumed
  Real _xPrev, _yPrev;      // the filter's previous input and output
  std::vector<Real> _frame; // filtered, squared samples of the frame being filled
  Real _Vms;
  std::vector<Real> _VdB;   // dB level of each completed frame

 public:
  DynamicComplexity();
  ~DynamicComplexity() {
    delete _dynAlgo;
  }

  void declareParameters() {
    declareParameter("sampleRate", "the sampling rate of the audio signal [Hz]", "(0,inf)", 44100.);
    declareParameter("frameSize", "the frame size [s]", "(0,inf)", 0.2);
  }

  void configure();
  void reset();
  void consume();
  void finalProduce();

  static const char* name;
  static const char* category;
  static const char* description;
};

} // namespace streaming
} // namespace essentia

#endif // ESSENTIA_DYNAMICCOMPLEXITY_H
