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

#ifdef _WIN32
#include <windows.h>
#endif

#include <essentia/essentia.h>
#include <essentia/algorithm.h>
#include <essentia/algorithmfactory.h> 
#include <essentia/utils/extractor_music/extractor_version.h>
#include "music_extractor/extractor_utils.h"

#include "credit_libav.h"

using namespace std;
using namespace essentia;
using namespace essentia::standard;


void usage(char *progname) {
    cout << "Error: wrong number of arguments" << endl;
    cout << "Usage: " << progname << " input_audiofile output_textfile [profile] [--frames-bin <path>]" << endl;
    cout << endl
         << "  --frames-bin <path>  when the profile sets outputFrames, write the frame-level pool" << endl
         << "                       as an ESPB binary file at <path> instead of the '_frames'" << endl
         << "                       YAML/JSON file; the main output is unaffected. Optional" << endl
         << "                       profile key framesInclude: [prefix, ...] filters the descriptors." << endl;
    cout << endl << "Music extractor version '" << MUSIC_EXTRACTOR_VERSION << "'" << endl
         << "build '" << MUSIC_EXTRACTOR_BUILD_ID << "'" << endl
         << "built with Essentia version " << essentia::version_git_sha << endl;
    creditLibAV();

    exit(1);
}


// Parses argv after the program name: input output [profile] [--frames-bin <path>].
// Returns false on any shape it does not recognise (caller prints usage).
bool parseArgs(int argc, char** argv, string& audioFilename, string& outputFilename,
               string& profileFilename, string& framesBinFilename) {
  if (argc < 3) return false;
  audioFilename = argv[1];
  outputFilename = argv[2];
  int i = 3;
  if (i < argc && string(argv[i]) != "--frames-bin") {
    profileFilename = argv[i];
    ++i;
  }
  if (i < argc) {
    if (string(argv[i]) != "--frames-bin" || i + 1 >= argc) return false;
    framesBinFilename = argv[i + 1];
    i += 2;
  }
  return i == argc;
}


int essentia_main(string audioFilename, string outputFilename, string profileFilename, string framesBinFilename) {
  // Returns: 1 on essentia error

  try {
    essentia::init();

    cout.precision(10); // TODO ????

    Pool options;
    setExtractorDefaultOptions(options);
    setExtractorOptions(profileFilename, options);

    Algorithm* extractor = AlgorithmFactory::create("MusicExtractor",
                                                    "profile", profileFilename);

    Pool results;
    Pool resultsFrames;

    extractor->input("filename").set(audioFilename);
    extractor->output("results").set(results);
    extractor->output("resultsFrames").set(resultsFrames);

    extractor->compute();

    mergeValues(results, options);

    // Identity of this binary, additive to the main output only.
    results.set("metadata.version.extractor_build", MUSIC_EXTRACTOR_BUILD_ID);

    outputToFile(results, outputFilename, options);
    if (options.value<Real>("outputFrames")) {
      if (framesBinFilename.empty()) {
        outputToFile(resultsFrames, outputFilename+"_frames", options);
      }
      else {
        outputToBinaryFile(resultsFrames, framesBinFilename, options, MUSIC_EXTRACTOR_BUILD_ID);
      }
    }
    else if (!framesBinFilename.empty()) {
      cerr << "Warning: --frames-bin given but the profile does not set outputFrames; no frames file written" << endl;
    }
    delete extractor;
    essentia::shutdown();
  }
  catch (EssentiaException& e) {
    cerr << e.what() << endl;
    return 1;
  }
  catch (const std::bad_alloc& e) {
    cerr << "bad_alloc exception: Out of memory " << e.what() << endl;
    return 1;
  }

  return 0;
}

#ifdef _WIN32
int main(int win32_argc, char **win32_argv)
{
  int i, argc = 0, buffsize = 0, offset = 0;
  char **utf8_argv, *utf8_argv_ptr;
  wchar_t **argv;

  argv = CommandLineToArgvW(GetCommandLineW(), &argc);

  buffsize = 0;
  for (i = 0; i < argc; i++) {
      buffsize += WideCharToMultiByte(CP_UTF8, 0, argv[i], -1, NULL, 0, NULL, NULL);
  }

  size_t len = sizeof(char *) * (argc + 1) + buffsize;
  utf8_argv = (char**)malloc(len);
  memset(utf8_argv, 0, len);
  utf8_argv_ptr = (char *)utf8_argv + sizeof(char *) * (argc + 1);

  for (i = 0; i < argc; i++) {
      utf8_argv[i] = &utf8_argv_ptr[offset];
      offset += WideCharToMultiByte(CP_UTF8, 0, argv[i], -1, &utf8_argv_ptr[offset], buffsize - offset, NULL, NULL);
  }

  LocalFree(argv);

  string audioFilename, outputFilename, profileFilename, framesBinFilename;

  if (!parseArgs(argc, utf8_argv, audioFilename, outputFilename, profileFilename, framesBinFilename)) {
    usage(utf8_argv[0]);
  }

  return essentia_main(audioFilename, outputFilename, profileFilename, framesBinFilename);
}

#else
int main(int argc, char* argv[]) {

  string audioFilename, outputFilename, profileFilename, framesBinFilename;

  if (!parseArgs(argc, argv, audioFilename, outputFilename, profileFilename, framesBinFilename)) {
    usage(argv[0]);
  }

  return essentia_main(audioFilename, outputFilename, profileFilename, framesBinFilename);
}
#endif
