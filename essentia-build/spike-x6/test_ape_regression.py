#!/usr/bin/env python3
"""APE decoding and full-extractor regressions; existing binaries only, offline.

See ape-regression.md for the pinned fixture and example invocations.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
INPUT_SHA256 = "6a7b79a6d530e9847c18119d627bd43c8d27dcefb3ec7ec979b9b6306e34ac15"
PCM_SHA256 = "6ead09fa12f54f212c63415945667c363b2f05345f8f0398f2d848ca365c86db"
PCM_BYTES = 21337344  # FFmpeg 7.1.1: 2,667,168 stereo float32 frames.
DURATION = 60.48
ARGS = argparse.Namespace(
    input=HERE / "corpus/ape_luckynight.ape",
    extractor=HERE.parent / "output-x64.6/essentia_streaming_extractor_music.exe",
    loader=HERE / "out/tools-output-x64.6/essentia_standard_audioloader_dump.exe")


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


class ApeRegression(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not ARGS.input.is_file():
            raise AssertionError("Missing APE fixture; see ape-regression.md")
        if sha256(ARGS.input) != INPUT_SHA256:
            raise AssertionError("APE fixture differs from the pinned reference input")
        for tool in (ARGS.extractor, ARGS.loader):
            if not tool.is_file():
                raise AssertionError("Missing existing executable: " + str(tool))

    def setUp(self):
        self.workspace = tempfile.TemporaryDirectory(prefix="essentia-ape-regression-")
        self.addCleanup(self.workspace.cleanup)
        self.work = Path(self.workspace.name)

    def execute(self, tool, *arguments):
        result = subprocess.run([str(tool), *map(str, arguments)], cwd=self.work,
                                capture_output=True, text=True, errors="replace")
        self.assertEqual(result.returncode, 0,
                         "Executable failed:\n" + (result.stderr + result.stdout)[-4000:])
        return result

    def test_loader_keeps_every_sample_in_multi_frame_packets(self):
        output = self.work / "decoded.f32"
        self.execute(ARGS.loader, ARGS.input, output)
        self.assertEqual(output.stat().st_size, PCM_BYTES, "Missing or extra decoded samples")
        self.assertEqual(sha256(output), PCM_SHA256, "PCM differs from FFmpeg 7.1.1")

    def extract(self, sample_rate=None):
        output = self.work / "features.json"
        arguments = [ARGS.input, output]
        if sample_rate is not None:
            profile = self.work / "profile.yaml"
            profile.write_text("analysisSampleRate: %d\n" % sample_rate, encoding="utf-8")
            arguments.append(profile)
        self.execute(ARGS.extractor, *arguments)
        result = json.loads(output.read_text(encoding="utf-8"))
        audio = result["metadata"]["audio_properties"]
        self.assertEqual(audio["codec"], "ape")
        self.assertEqual(audio["sample_rate"], 44100)
        self.assertEqual(audio["number_channels"], 2)
        self.assertAlmostEqual(audio["length"], DURATION, delta=1e-4)
        self.assertAlmostEqual(audio["analysis"]["length"], DURATION, delta=1e-4)
        self.assertEqual(audio["analysis"]["sample_rate"], sample_rate or 44100)
        self.assertIn("rhythm", result)
        self.assertIn("lowlevel", result)

    def test_full_extractor_drains_output_buffers_and_final_partial_packet(self):
        self.extract()

    def test_full_extractor_with_resampling_keeps_full_duration(self):
        self.extract(sample_rate=48000)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, default=ARGS.input)
    parser.add_argument("--extractor", type=Path, default=ARGS.extractor)
    parser.add_argument("--loader", type=Path, default=ARGS.loader)
    ARGS = parser.parse_args()
    ARGS.input, ARGS.extractor, ARGS.loader = (
        value.resolve() for value in (ARGS.input, ARGS.extractor, ARGS.loader))
    unittest.main(argv=[__file__], verbosity=2)
