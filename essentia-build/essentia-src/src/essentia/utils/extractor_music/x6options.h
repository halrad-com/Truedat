/*
 * truedat x6 (drop 2): the music extractor's profile switches.
 *
 * A switch is a profile key holding 0 or 1. ABSENT MEANS ON, so a profile
 * written for output-x64.5 - or no profile at all, the 2-arg call - gets the
 * full extractor unchanged; the SCAN profile sets the ones it cuts to 0
 * (essentia-build/spike-x6/scan-profile.yaml).
 *
 *   x6.melbands128   build the 128-band mel branch (read by nothing truedat
 *                    consumes, main output or frames)
 *   x6.bandsStorage  store the per-frame melbands / erbbands / barkbands
 *                    vectors in the pool; their shape, flatness and crest
 *                    descriptors still consume them in-graph either way
 *   x6.md5           MD5 over the encoded packets
 *                    (metadata.audio_properties.md5_encoded)
 */

#ifndef ESSENTIA_X6OPTIONS_H
#define ESSENTIA_X6OPTIONS_H

#include "pool.h"

namespace essentia {

inline bool x6on(const Pool& options, const std::string& key) {
  return !options.contains<Real>(key) || options.value<Real>(key) != 0;
}

} // namespace essentia

#endif // ESSENTIA_X6OPTIONS_H
