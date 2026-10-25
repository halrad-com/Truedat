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

// truedat x7. Every expression below that produces a value is HPCP's own
// (hpcp.cpp), with the same types, the same includes and the same using
// directives, so name lookup picks the same functions; only the order of
// evaluation ACROSS the two outputs differs, never within one. The build is
// -O2 -ffp-contract=off without fast-math, so identical expressions on
// identical inputs round identically.

#include "hpcpfused.h"
#include "essentiamath.h"

using namespace std;
using namespace essentia;
using namespace standard;

const char* HPCPFused::name = "HPCPFused";
const char* HPCPFused::category = "Tonal";
const char* HPCPFused::description = DOC("Computes two Harmonic Pitch Class Profiles of the same configuration and different sizes ('size' and 'sizeHighRes') in one pass over the spectral peaks. Each output is identical to HPCP's output for the same parameters and that size; the per-peak and per-harmonic values the two sizes share are computed once. See HPCP for the parameters, the exceptions and the references.");


const Real HPCPFused::precision = 0.00001;

void HPCPFused::configure() {
  _size = parameter("size").toInt();
  _sizeHighRes = parameter("sizeHighRes").toInt();

  if (_size % 12 != 0 || _sizeHighRes % 12 != 0) {
    throw EssentiaException("HPCPFused: The size parameters are not a multiple of 12.");
  }

  _windowSize = parameter("windowSize").toReal();

  if (_windowSize * _size/12 < 1.0 || _windowSize * _sizeHighRes/12 < 1.0) {
    throw EssentiaException("HPCPFused: Your windowSize needs to span at least one hpcp bin (windowSize >= 12/size) for both sizes");
  }

  _referenceFrequency = parameter("referenceFrequency").toReal();
  _nHarmonics = parameter("harmonics").toInt();

  _minFrequency = parameter("minFrequency").toReal();
  _maxFrequency = parameter("maxFrequency").toReal();

  if ((_maxFrequency - _minFrequency) < 200.0) {
    throw EssentiaException("HPCPFused: Minimum and maximum frequencies are too close");
  }

  _splitFrequency = parameter("bandSplitFrequency").toReal();
  _bandPreset = parameter("bandPreset").toBool();

  if (_bandPreset) {
    if ((_splitFrequency - _minFrequency) < 200.0) {
      throw EssentiaException("HPCPFused: Low band frequency range too small");
    }
    if ((_maxFrequency - _splitFrequency) < 200.0) {
      throw EssentiaException("HPCPFused: High band frequency range too small");
    }
  }

  string weightType = toLower(parameter("weightType").toString());
  if      (weightType == "none") _weightType = NONE;
  else if (weightType == "cosine") _weightType = COSINE;
  else if (weightType == "squaredcosine") _weightType = SQUARED_COSINE;
  else throw EssentiaException("Invalid weight type for HPCPFused: ", weightType);

  _nonLinear = parameter("nonLinear").toBool();
  _maxShifted = parameter("maxShifted").toBool();

  string normalized = toLower(parameter("normalized").toString());
  if (normalized == "none") _normalized = N_NONE;
  if (normalized == "unitsum") _normalized = N_UNIT_SUM;
  if (normalized == "unitmax") _normalized = N_UNIT_MAX;


  if (_nonLinear && _normalized != N_UNIT_MAX) {
    throw EssentiaException("HPCPFused: Cannot apply non-linear filter when HPCP vector is not normalized to unit max.");
  }

  initHarmonicContributionTable();
}


// HPCP::initHarmonicContributionTable, unchanged
void HPCPFused::initHarmonicContributionTable() {
  _harmonicPeaks.clear();

  for (int i = 0; i <= _nHarmonics; i++) {
    Real semitone = 12.0 * log2(i+1.0);
    Real octweight = max(1.0 , ( semitone /12.0)*0.5);

    while (semitone >= 12.0-precision) {
      semitone -= 12.0;
    }

    vector<HarmonicPeak>::iterator it;
    for (it = _harmonicPeaks.begin(); it != _harmonicPeaks.end(); it++) {
      if ((*it).semitone > semitone-precision && (*it).semitone < semitone+precision) break;
    }

    if (it == _harmonicPeaks.end()) {
      _harmonicPeaks.push_back(HarmonicPeak(semitone, (1.0 / octweight)));
    }
    else {
      (*it).harmonicStrength += (1.0 / octweight);
    }
  }
}


// HPCP::addContributionWithWeight from the line after its pcpBinF onward:
// pcpBinF is passed in, computed by the caller as HPCP computes it,
// log2(freq / _referenceFrequency) * (Real)pcpSize, with the log2 shared
void HPCPFused::addWeighted(Real pcpBinF, Real mag_lin, vector<Real>& hpcp, Real harmonicWeight) const {
  int pcpSize = hpcp.size();
  Real resolution = pcpSize / 12; // # of bins / semitone

  int leftBin = (int)ceil(pcpBinF - resolution * _windowSize / 2.0);
  int rightBin = (int)floor(pcpBinF + resolution * _windowSize / 2.0);

  assert(rightBin-leftBin >= 0);

  for (int i=leftBin; i<=rightBin; i++) {

    Real distance = abs(pcpBinF - (Real)i)/resolution;
    Real normalizedDistance = distance/_windowSize;
    Real weight = 0.;

    if (_weightType == COSINE) {
      weight = cos(M_PI*normalizedDistance);
    }
    else if (_weightType == SQUARED_COSINE) {
      weight = cos(M_PI*normalizedDistance);
      weight *= weight;
    }

    int iwrapped = i % pcpSize;
    if (iwrapped < 0) iwrapped += pcpSize;

    hpcp[iwrapped] += weight * (mag_lin*mag_lin) * harmonicWeight * harmonicWeight;
  }
}


// HPCP::addContributionWithoutWeight after its freq check and its octave
// (Real octave = log2(freq/_referenceFrequency)), which the caller computes
void HPCPFused::addUnweighted(Real octave, Real mag_lin, vector<Real>& hpcp, Real harmonicWeight) const {
  int pcpsize = hpcp.size();

  int pcpbin = (int)round(pcpsize * octave);  // bin distance from ref frequency

  pcpbin %= pcpsize;
  if (pcpbin < 0)
    pcpbin += pcpsize;

  hpcp[pcpbin] += mag_lin * mag_lin * harmonicWeight * harmonicWeight;
}


// HPCP::compute from "Normalize the HPCP vector" to the end, for one output
void HPCPFused::finish(vector<Real>& hpcp, vector<Real>& hpcp_LO, vector<Real>& hpcp_HI) const {
  if (_bandPreset) {
    if (_normalized == N_UNIT_MAX) {
      normalize(hpcp_LO);
      normalize(hpcp_HI);
    }
    else if (_normalized == N_UNIT_SUM) {
      E_WARNING("HPCPFused: applying band preset together with unit sum normalization was not tested.");
      normalizeSum(hpcp_LO);
      normalizeSum(hpcp_HI);
    }

    for (int i=0; i<(int)hpcp.size(); i++) {
      hpcp[i] = hpcp_LO[i] + hpcp_HI[i];
    }
  }

  if (_normalized == N_UNIT_MAX) {
    normalize(hpcp);
  }
  else if (_normalized == N_UNIT_SUM) {
    normalizeSum(hpcp);
  }

  if (_nonLinear) {
    for (int i=0; i<(int)hpcp.size(); i++) {
      hpcp[i] = sin(hpcp[i] * M_PI * 0.5);
      hpcp[i] *= hpcp[i];
      if (hpcp[i] < 0.6) {
        hpcp[i] *= hpcp[i]/0.6 * hpcp[i]/0.6;
      }
    }
  }

  if (_maxShifted) {
    int idxMax = argmax(hpcp);
    vector<Real> hpcp_bak = hpcp;
    for (int i=idxMax; i<(int)hpcp.size(); i++) {
      hpcp[i-idxMax] = hpcp_bak[i];
    }
    int offset = hpcp.size() - idxMax;
    for (int i=0; i<idxMax; i++) {
      hpcp[i+offset] = hpcp_bak[i];
    }
  }
}


void HPCPFused::compute() {
  const vector<Real>& frequencies = _frequencies.get();
  const vector<Real>& magnitudes = _magnitudes.get();
  vector<Real>& hpcpA = _hpcp.get();
  vector<Real>& hpcpB = _hpcpHighRes.get();

  if (magnitudes.size() != frequencies.size()) {
    throw EssentiaException("HPCPFused: Frequency and magnitude input vectors are not of equal size");
  }

  hpcpA.resize(_size);
  fill(hpcpA.begin(), hpcpA.end(), (Real)0.0);
  hpcpB.resize(_sizeHighRes);
  fill(hpcpB.begin(), hpcpB.end(), (Real)0.0);

  vector<Real> loA, hiA, loB, hiB;

  if (_bandPreset) {
    loA.resize(_size);        fill(loA.begin(), loA.end(), (Real)0.0);
    hiA.resize(_size);        fill(hiA.begin(), hiA.end(), (Real)0.0);
    loB.resize(_sizeHighRes); fill(loB.begin(), loB.end(), (Real)0.0);
    hiB.resize(_sizeHighRes); fill(hiB.begin(), hiB.end(), (Real)0.0);
  }

  // Each output receives its contributions in HPCP's order - peak, then
  // harmonic position, then bin - so each sum is HPCP's sum
  for (int i=0; i<(int)frequencies.size(); i++) {
    Real freq = frequencies[i];
    Real mag_lin = magnitudes[i];

    if (freq >= _minFrequency && freq <= _maxFrequency) {
      vector<Real>& a = _bandPreset ? ((freq < _splitFrequency) ? loA : hiA) : hpcpA;
      vector<Real>& b = _bandPreset ? ((freq < _splitFrequency) ? loB : hiB) : hpcpB;

      vector<HarmonicPeak>::const_iterator it;
      for (it=_harmonicPeaks.begin(); it!= _harmonicPeaks.end(); it++) {
        // HPCP::addContribution's hypothesised fundamental, once for both sizes
        Real f = freq * pow(2., -(*it).semitone / 12.0);
        Real harmonicWeight = (*it).harmonicStrength;

        if (_weightType != NONE) {
          // the same call HPCP makes; auto keeps its return type, so the
          // product with each size is HPCP's product
          auto octave = log2(f / _referenceFrequency);
          addWeighted(octave * (Real)(int)a.size(), mag_lin, a, harmonicWeight);
          addWeighted(octave * (Real)(int)b.size(), mag_lin, b, harmonicWeight);
        }
        else if (f > 0) {   // HPCP::addContributionWithoutWeight returns for freq <= 0
          Real octave = log2(f/_referenceFrequency);
          addUnweighted(octave, mag_lin, a, harmonicWeight);
          addUnweighted(octave, mag_lin, b, harmonicWeight);
        }
      }
    }
  }

  finish(hpcpA, loA, hiA);
  finish(hpcpB, loB, hiB);
}
