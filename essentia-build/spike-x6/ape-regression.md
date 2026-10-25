# APE regression

`test_ape_regression.py` tests real executables without compiling or making network requests. It fails on a missing fixture, missing executable, nonzero extractor exit, incomplete analyzed duration, or a PCM mismatch. It covers complete decoder output, ordinary full extraction, and full extraction with 48 kHz analysis resampling. Outputs use a fresh temporary directory per case.

The fixture is FFmpeg's public [luckynight.ape](https://samples.ffmpeg.org/A-codecs/lossless/luckynight.ape), stored locally as `corpus/ape_luckynight.ape` (audio is ignored by Git, like the rest of the corpus). Its SHA-256 is `6a7b79a6d530e9847c18119d627bd43c8d27dcefb3ec7ec979b9b6306e34ac15`, size 6,510,317 bytes. Obtain this exact fixture explicitly when setting up a new checkout; the test never downloads it automatically.

The local `manifest.tsv` and `real-files.tsv` include `r_ape`, added concurrently by the backend developer and pointing to the original downloaded copy in the repository. The initial duplicate `r_ape_luckynight` row was removed so the fixture counts once. The test's corpus copy has the same pinned hash. The shared parity and repeat runners consume the manifest, and `make-corpus.sh` retains the real-file row when regenerating it. Regenerating the corpus refreshes `corpus-md5.txt` through the existing process; do not overwrite another agent's corpus evidence during an active run.

The expected audio is 44,100 Hz stereo, 2,667,168 sample frames, 60.48 seconds. FFmpeg 7.1.1 decoded it to 21,337,344 interleaved float32 bytes with SHA-256 `6ead09fa12f54f212c63415945667c363b2f05345f8f0398f2d848ca365c86db`. These values are independent of the extractor under test.

From this directory, with Python 3.11 or newer:

```text
python test_ape_regression.py
python test_ape_regression.py --extractor path/to/candidate.exe --loader path/to/candidate-loader.exe
```

The original `.5` loader retained only 175,104 sample frames; its full extractor reported success with a 3.970612-second analysis. Decoder-only `.6` keeps all samples and passes exact PCM parity, but its full extractor fails in `StereoDemuxer::left` on an oversized EOF acquisition. A passing loader check alone does not establish that the full extractor works.
