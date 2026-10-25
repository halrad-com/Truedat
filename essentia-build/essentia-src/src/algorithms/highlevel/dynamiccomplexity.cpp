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

#include "dynamiccomplexity.h"
#include "essentiamath.h"
#include <list>

using namespace std;

namespace essentia {
namespace standard {

const char* DynamicComplexity::name = "DynamicComplexity";
const char* DynamicComplexity::category = "Loudness/dynamics";
const char* DynamicComplexity::description = DOC("This algorithm computes the dynamic complexity defined as the average absolute deviation from the global loudness level estimate on the dB scale. It is related to the dynamic range and to the amount of fluctuation in loudness present in a recording. Silence at the beginning and at the end of a track are ignored in the computation in order not to deteriorate the results.\n\n"
"References:\n"
"  [1] S. Streich, Music complexity: a multi-faceted description of audio\n"
"  content, UPF, Barcelona, Spain, 2007.");


void DynamicComplexity::configure() {
  _sampleRate = parameter("sampleRate").toReal();
  _frameSize = int(floor(parameter("frameSize").toReal() * _sampleRate));
}

const Real DynamicComplexity::kB0 = 0.98595;
const Real DynamicComplexity::kB1 = -0.98595;
const Real DynamicComplexity::kA1 = -0.9719;

void DynamicComplexity::smearing(vector<Real>& weight, Real& c, Real& Vweight) const {
  c = exp(-1.0/(0.035*_sampleRate));

  // create weight vector
  weight.assign(_frameSize, (Real)0.0);
  Vweight = 1.0;
  for (int i=_frameSize-1; i>=0; i--) {
    weight[i] = Vweight;
    Vweight *= c;
  }
}

void DynamicComplexity::compute() {
  const vector<Real>& signal = _signal.get();
  Real& complexity = _complexity.get();
  Real& loudness = _loudness.get();

  if (signal.empty()) {
    complexity = 0;
    loudness = DB_SILENCE_CUTOFF;
    return;
  }

  Real c, Vweight;
  vector<Real> weight;
  smearing(weight, c, Vweight);

  // cheap B-curve loudness compensation
  vector<Real> samps;
  filter(samps, signal);

  // compute energy per frame and apply smearing function
  int framenum = signal.size() / _frameSize;
  Real Vms = 0.0;
  vector<Real> VdB(framenum);

  int nSamples = signal.size();

  // energy
  for (int i=0; i<nSamples; i++) {
    samps[i] = samps[i] * samps[i];
  }

  for (int i=0; i<framenum; i++) {
    Vms = nextLevel(Vms, Vweight, c, weight, samps.begin()+i*_frameSize);
    VdB[i] = pow2db(Vms); //20 * log10(sqrt(Vms) + 1e-9);
  }

  fromLevels(VdB, complexity, loudness);
}

void DynamicComplexity::fromLevels(vector<Real>& VdB, Real& complexity, Real& loudness) const {
  // erase silence at beginning
  int framenum = VdB.size();
  int beginIdx = 0;
  while ((beginIdx < framenum) && (VdB[beginIdx] == DB_SILENCE_CUTOFF)) beginIdx++;
  VdB.erase(VdB.begin(), VdB.begin() + beginIdx);

  // erase silence at end
  int endIdx = VdB.size() - 1;
  while ((endIdx >= 0) && (VdB[endIdx] == DB_SILENCE_CUTOFF)) endIdx--;
  if (endIdx == -1) // if we don't do this it crashes in VS8.0
    VdB.clear();
  else
    VdB.erase(VdB.begin()+endIdx+1, VdB.end());

  loudness = 0.0;
  complexity = 0.0;

  if (!VdB.empty()) {
    vector<Real> u(VdB.size());
    for (int i=0; i<int(u.size()); i++) u[i] = pow((Real)0.9, -VdB[i]);
    Real s = accumulate(u.begin(), u.end(), 0.0);
    for (int i=0; i<int(u.size()); i++) u[i] /= s;

    loudness = inner_product(u.begin(), u.end(), VdB.begin(), 0.0);

    for (int i=0; i<int(VdB.size()); ++i) {
      complexity += fabs(VdB[i] - loudness);
    }
    complexity /= VdB.size();
  }
  else { // silent input
    loudness = DB_SILENCE_CUTOFF;
    complexity = 0.0;
  }

  // normalization
  // loudness levels are limited to -100 dB .. 0 dB so if a signal would be
  // half of the time totally silent and the other half at full scale, we
  // would get a value of 45 as the Dynamic Complexity then a safe
  // normalization would be using 45.

}

void DynamicComplexity::filter(vector<Real>& result, const vector<Real>& input) const {
  result.resize(input.size());

  result[0] = filterFirst(input[0]);
  for (int i=1; i<(int)input.size(); i++) {
    result[i] = filterStep(input[i], input[i-1], result[i-1]);
  }
}

} // namespace standard
} // namespace essentia

#include "algorithmfactory.h"

namespace essentia {
namespace streaming {

DynamicComplexity::DynamicComplexity() : _dynAlgo(0), _frameSize(0), _c(0), _Vweight(0),
                                         _n(0), _xPrev(0), _yPrev(0), _Vms(0) {

  standard::Algorithm* algo = standard::AlgorithmFactory::create("DynamicComplexity");
  _dynAlgo = dynamic_cast<standard::DynamicComplexity*>(algo);
  if (!_dynAlgo) {
    delete algo;
    throw EssentiaException("DynamicComplexity: the factory's standard DynamicComplexity is not standard::DynamicComplexity");
  }

  declareInputStream(_signal, "signal", "the input audio signal");
  declareOutputResult(_complexity, "dynamicComplexity", "the dynamic complexity coefficient");
  declareOutputResult(_loudness, "loudness", "an estimate of the loudness [dB]");
  reset();
}

void DynamicComplexity::configure() {
  // through the base: standard::DynamicComplexity's own configure() hides the name/value overloads
  standard::Algorithm* base = _dynAlgo;
  base->configure("sampleRate", parameter("sampleRate").toInt(),
                  "frameSize", parameter("frameSize").toReal());
  _frameSize = _dynAlgo->frameSize();
  _dynAlgo->smearing(_weight, _c, _Vweight);
  reset();
}

void DynamicComplexity::reset() {
  Algorithm::reset();             // stop flag + output buffers, as the composite did
  AccumulatorAlgorithm::reset();
  _dynAlgo->reset();
  _n = 0;
  _xPrev = 0;
  _yPrev = 0;
  _frame.clear();
  _frame.reserve(max(_frameSize, 0));
  _Vms = 0.0;
  _VdB.clear();
}

void DynamicComplexity::consume() {
  const vector<Real>& signal = *((const vector<Real>*)_signal.getTokens());

  // the standard form, one sample at a time: filter the whole signal, square it, then smear
  // each whole frame from sample 0 (a partial last frame is never read there, nor here)
  for (int i=0; i<(int)signal.size(); i++) {
    Real x = signal[i];
    Real y = (_n == 0) ? standard::DynamicComplexity::filterFirst(x)
                       : standard::DynamicComplexity::filterStep(x, _xPrev, _yPrev);
    _xPrev = x;
    _yPrev = y;
    _n++;
    _frame.push_back(y * y);
    if ((int)_frame.size() == _frameSize) {
      _Vms = standard::DynamicComplexity::nextLevel(_Vms, _Vweight, _c, _weight, _frame.begin());
      _VdB.push_back(pow2db(_Vms));
      _frame.clear();
    }
  }
}

void DynamicComplexity::finalProduce() {
  Real complexity;
  Real loudness;
  if (_n == 0) {   // the standard form's empty-signal answer
    complexity = 0;
    loudness = DB_SILENCE_CUTOFF;
  }
  else {
    _dynAlgo->fromLevels(_VdB, complexity, loudness);
  }
  _complexity.push(complexity);
  _loudness.push(loudness);
}


} // namespace streaming
} // namespace essentia
