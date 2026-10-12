#ifndef EXTRACTOR_VERSION_H_
#define EXTRACTOR_VERSION_H_

#define MUSIC_EXTRACTOR_VERSION "music 2.0"
#define MUSIC_EXTRACTOR_HL_VERSION "music_highlevel 1.0"

// Identity of THIS compiled extractor binary, written to the main output as
// metadata.version.extractor_build (truedat reads the build from the output
// instead of an MD5 table). Bump with every output-x64.N build.
#define MUSIC_EXTRACTOR_BUILD_ID "output-x64.5"

#endif /* EXTRACTOR_VERSION_H_ */
