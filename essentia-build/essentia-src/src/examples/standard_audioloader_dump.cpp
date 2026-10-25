/*
 * truedat x6 decoder-parity tool (not part of any shipped binary).
 *
 * Decodes a file with Essentia's AudioLoader and writes the PCM exactly as the
 * loader emits it: interleaved float32 little-endian at the native rate and
 * native channel count. Mono writes the left channel only, because the loader
 * fills only left() for mono. One summary line goes to stdout so the parity
 * script can compare the sample count without reading the file.
 *
 * The reference side is `ffmpeg -i <in> -map 0:a:0 -c:a pcm_f32le -f f32le`
 * with no -ar / -ac, which is the same format-only conversion the loader does
 * (in rate == out rate, in layout == out layout). See
 * essentia-build/spike-x6/README.md for the delay/padding rules.
 *
 *   essentia_standard_audioloader_dump <input> <output.f32>
 *
 * Exit: 0 decoded, 1 usage, 2 loader threw (message on stderr), 3 write failed.
 * argv is the process's ANSI argv, so corpus paths are kept ASCII.
 */

#include <cstdio>
#include <iostream>
#include <vector>
#include <essentia/algorithmfactory.h>

using namespace std;
using namespace essentia;
using namespace essentia::standard;

int main(int argc, char* argv[]) {
  if (argc != 3) {
    cerr << "usage: " << argv[0] << " <input> <output.f32>" << endl;
    return 1;
  }

  essentia::init();
  int rc = 0;

  try {
    Algorithm* loader = AlgorithmFactory::create("AudioLoader",
                                                 "filename", string(argv[1]));
    vector<StereoSample> audio;
    Real sampleRate = 0;
    int channels = 0;
    string md5, codec;
    int bitRate = 0;
    loader->output("audio").set(audio);
    loader->output("sampleRate").set(sampleRate);
    loader->output("numberChannels").set(channels);
    loader->output("md5").set(md5);
    loader->output("bit_rate").set(bitRate);
    loader->output("codec").set(codec);
    loader->compute();
    delete loader;

    FILE* out = fopen(argv[2], "wb");
    if (!out) {
      cerr << "ERROR: cannot open " << argv[2] << " for writing" << endl;
      rc = 3;
    }
    else {
      vector<float> interleaved;
      interleaved.reserve(audio.size() * (channels == 1 ? 1 : 2));
      for (size_t i = 0; i < audio.size(); ++i) {
        interleaved.push_back(audio[i].left());
        if (channels != 1) interleaved.push_back(audio[i].right());
      }
      size_t written = interleaved.empty() ? 0 :
        fwrite(&interleaved[0], sizeof(float), interleaved.size(), out);
      if (fclose(out) != 0 || written != interleaved.size()) {
        cerr << "ERROR: short write to " << argv[2] << endl;
        rc = 3;
      }
    }

    printf("samples=%u rate=%.0f channels=%d codec=%s\n",
           (unsigned)audio.size(), sampleRate, channels, codec.c_str());
  }
  catch (EssentiaException& e) {
    cerr << "ERROR: " << e.what() << endl;
    rc = 2;
  }

  essentia::shutdown();
  return rc;
}
