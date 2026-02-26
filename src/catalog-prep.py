#!/usr/bin/env python3
"""
catalog-prep.py - Build a synthetic library catalog from AcousticBrainz + MusicBrainz data.

Downloads AcousticBrainz sample JSON (100k Essentia feature documents) and MusicBrainz
JSON dumps (release, artist, release-group), joins them by MusicBrainz Recording ID,
and outputs a gzipped JSONL catalog for use with `truedat.exe --synthesize`.

Usage:
    python catalog-prep.py --download          # Download data dumps
    python catalog-prep.py --build             # Build catalog from downloaded data
    python catalog-prep.py --download --build  # Download + build in one pass
    python catalog-prep.py --stats             # Show catalog statistics

Data is cached in ../data/ (relative to this script).
"""

import argparse
import gzip
import json
import logging
import os
import re
import sys
import tarfile
from pathlib import Path

import requests
import zstandard as zstd
from tqdm import tqdm

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("catalog-prep")

SCRIPT_DIR = Path(__file__).resolve().parent
DATA_DIR = SCRIPT_DIR.parent / "data"
AB_DIR = DATA_DIR / "acousticbrainz"
MB_DIR = DATA_DIR / "musicbrainz"
CATALOG_PATH = DATA_DIR / "synthlib-catalog.jsonl.gz"

# AcousticBrainz sample dump (100k full Essentia JSON documents, 2 GB)
AB_SAMPLE_URL = (
    "https://data.metabrainz.org/pub/musicbrainz/acousticbrainz/dumps/"
    "acousticbrainz-sample-json-20220623/"
    "acousticbrainz-lowlevel-sample-json-20220623-0.tar.zst"
)

# MusicBrainz JSON dumps (latest as of 2026-02-25)
MB_BASE_URL = (
    "https://data.metabrainz.org/pub/musicbrainz/data/json-dumps/20260225-001001"
)
MB_DUMPS = {
    "release": f"{MB_BASE_URL}/release.tar.xz",
    "artist": f"{MB_BASE_URL}/artist.tar.xz",
    "release-group": f"{MB_BASE_URL}/release-group.tar.xz",
}


def download_file(url: str, dest: Path, description: str = None):
    """Download a file with resume support and progress bar.

    Args:
        url: URL to download from.
        dest: Local path to save the file.
        description: Label for the progress bar (defaults to filename).
    """
    CHUNK_SIZE = 8192
    TIMEOUT = 30

    dest.parent.mkdir(parents=True, exist_ok=True)
    label = description or dest.name

    # Check remote file size via HEAD request
    head = requests.head(url, timeout=TIMEOUT, allow_redirects=True)
    head.raise_for_status()
    remote_size = int(head.headers.get("content-length", 0))

    # Skip if already fully downloaded
    if dest.exists():
        local_size = dest.stat().st_size
        if remote_size and local_size >= remote_size:
            log.info("Already downloaded: %s (%s bytes)", label, local_size)
            return
    else:
        local_size = 0

    # Resume support: start from where we left off
    headers = {}
    if local_size > 0:
        headers["Range"] = f"bytes={local_size}-"
        log.info("Resuming %s from %s bytes", label, local_size)
    else:
        log.info("Downloading %s (%s bytes)", label, remote_size or "unknown size")

    resp = requests.get(url, headers=headers, stream=True, timeout=TIMEOUT)
    resp.raise_for_status()

    # If server honored Range request, content-length is the remaining bytes
    if resp.status_code == 206:
        total = local_size + int(resp.headers.get("content-length", 0))
    else:
        total = int(resp.headers.get("content-length", 0))
        # Server didn't honor Range — start from scratch
        local_size = 0

    mode = "ab" if resp.status_code == 206 else "wb"

    with (
        open(dest, mode) as f,
        tqdm(
            total=total or None,
            initial=local_size,
            unit="B",
            unit_scale=True,
            unit_divisor=1024,
            desc=label,
        ) as bar,
    ):
        for chunk in resp.iter_content(chunk_size=CHUNK_SIZE):
            if chunk:
                f.write(chunk)
                bar.update(len(chunk))

    final_size = dest.stat().st_size
    log.info("Completed: %s (%s bytes)", label, final_size)


def download_all():
    """Download all required data dumps."""
    log.info("Download directory: %s", DATA_DIR)

    # AcousticBrainz sample archive
    ab_filename = AB_SAMPLE_URL.rsplit("/", 1)[-1]
    ab_dest = AB_DIR / ab_filename
    download_file(AB_SAMPLE_URL, ab_dest, description="AcousticBrainz sample")

    # MusicBrainz JSON dumps
    for name, url in MB_DUMPS.items():
        mb_filename = url.rsplit("/", 1)[-1]
        mb_dest = MB_DIR / mb_filename
        download_file(url, mb_dest, description=f"MusicBrainz {name}")


def nav_path(obj, dotpath, default=None):
    """Navigate nested dicts by dot-separated path.

    Args:
        obj: Root dict to navigate.
        dotpath: Dot-separated key path (e.g. "rhythm.bpm").
        default: Value to return if any key is missing.

    Returns:
        The value at the path, or default if any key is missing.
    """
    current = obj
    for key in dotpath.split("."):
        if not isinstance(current, dict) or key not in current:
            return default
        current = current[key]
    return current


# Essentia JSON paths → camelCase output keys, with rounding precision.
# Each entry: (output_key, primary_path, fallback_path_or_None, round_digits_or_None)
_AB_FEATURE_MAP = [
    ("bpm", "rhythm.bpm", None, 1),
    ("key", "tonal.key_edma.key", "tonal.key_krumhansl.key", None),
    ("mode", "tonal.key_edma.scale", "tonal.key_krumhansl.scale", None),
    ("loudness", "lowlevel.loudness_ebu128.integrated", "lowlevel.average_loudness", 2),
    ("spectralCentroid", "lowlevel.spectral_centroid.mean", None, 1),
    ("spectralFlux", "lowlevel.spectral_flux.mean", None, 4),
    ("danceability", "rhythm.danceability", None, 4),
    ("onsetRate", "rhythm.onset_rate", None, 2),
    ("zeroCrossingRate", "lowlevel.zerocrossingrate.mean", None, 6),
    ("spectralRms", "lowlevel.spectral_rms.mean", None, 6),
    ("spectralFlatness", "lowlevel.spectral_flatness_db.mean", None, 6),
    ("dissonance", "lowlevel.dissonance.mean", None, 4),
    ("pitchSalience", "lowlevel.pitch_salience.mean", None, 4),
    ("chordsChangesRate", "tonal.chords_changes_rate", None, 4),
    ("mfcc0", "lowlevel.mfcc.mean", None, 4),  # first element of array
]


def extract_ab_features(json_obj):
    """Extract the 15 Essentia features that MBXHub's MoodEstimator uses.

    Args:
        json_obj: Parsed Essentia JSON document.

    Returns:
        Dict with camelCase keys matching mbxmoods.json format, or None
        if any required feature is missing.
    """
    result = {}

    for out_key, primary, fallback, precision in _AB_FEATURE_MAP:
        value = nav_path(json_obj, primary)
        if value is None and fallback:
            value = nav_path(json_obj, fallback)

        if value is None:
            return None

        # Special case: mfcc0 is the first element of the MFCC array
        if out_key == "mfcc0":
            if not isinstance(value, list) or len(value) == 0:
                return None
            value = value[0]

        # String features (key, mode) — must not be empty
        if out_key in ("key", "mode"):
            if not isinstance(value, str) or value == "":
                return None
            result[out_key] = value
            continue

        # Numeric features — must be a number
        if not isinstance(value, (int, float)):
            return None
        if precision is not None:
            value = round(value, precision)
        result[out_key] = value

    return result


# Regex to extract MBID from AcousticBrainz archive filenames.
# Format: lowlevel/ab/c/abcdef01-2345-6789-abcd-ef0123456789-N.json
_MBID_RE = re.compile(
    r"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})"
)


def load_ab_features():
    """Load AcousticBrainz features from the downloaded sample tar.zst archive.

    Decompresses the zstandard-compressed tar archive in streaming mode,
    parses each Essentia JSON document, and extracts the 15 features
    needed by MBXHub's MoodEstimator.

    Returns:
        dict mapping recording MBID (str) → feature dict.
    """
    archive_path = AB_DIR / "acousticbrainz-lowlevel-sample-json-20220623-0.tar.zst"
    if not archive_path.exists():
        log.error("AcousticBrainz archive not found: %s", archive_path)
        log.error("Run with --download first.")
        return {}

    log.info("Loading AcousticBrainz features from %s", archive_path.name)

    features = {}
    seen_mbids = set()
    skipped = 0

    dctx = zstd.ZstdDecompressor()

    with open(archive_path, "rb") as fh:
        reader = dctx.stream_reader(fh)
        tar = tarfile.open(fileobj=reader, mode="r|")

        with tqdm(desc="AcousticBrainz features", unit=" docs") as bar:
            for member in tar:
                if not member.isfile() or not member.name.endswith(".json"):
                    continue

                # Extract MBID from filename
                basename = os.path.basename(member.name)
                match = _MBID_RE.search(basename)
                if not match:
                    skipped += 1
                    bar.update(1)
                    continue

                mbid = match.group(1)

                # Skip duplicates (keep first per MBID)
                if mbid in seen_mbids:
                    skipped += 1
                    bar.update(1)
                    continue
                seen_mbids.add(mbid)

                # Parse JSON and extract features
                try:
                    f = tar.extractfile(member)
                    if f is None:
                        skipped += 1
                        bar.update(1)
                        continue
                    raw = f.read()
                    doc = json.loads(raw)
                except (json.JSONDecodeError, OSError) as exc:
                    log.debug("Skipping %s: %s", basename, exc)
                    skipped += 1
                    bar.update(1)
                    continue

                extracted = extract_ab_features(doc)
                if extracted is not None:
                    features[mbid] = extracted
                else:
                    skipped += 1

                bar.update(1)

        tar.close()

    log.info(
        "AcousticBrainz: %d features extracted, %d skipped",
        len(features),
        skipped,
    )
    return features


def build_catalog():
    """Build the synthetic library catalog."""
    raise NotImplementedError("Task 5")


def show_stats():
    """Show statistics about an existing catalog."""
    raise NotImplementedError("Task 5")


def main():
    parser = argparse.ArgumentParser(
        description="Build synthetic library catalog from AcousticBrainz + MusicBrainz data"
    )
    parser.add_argument("--download", action="store_true", help="Download data dumps")
    parser.add_argument(
        "--build", action="store_true", help="Build catalog from downloaded data"
    )
    parser.add_argument(
        "--stats", action="store_true", help="Show catalog statistics"
    )
    args = parser.parse_args()

    if not (args.download or args.build or args.stats):
        parser.print_help()
        return 1

    if args.download:
        download_all()
    if args.build:
        build_catalog()
    if args.stats:
        show_stats()
    return 0


if __name__ == "__main__":
    sys.exit(main())
