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
import sys
from pathlib import Path

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


def download_all():
    """Download all required data dumps."""
    raise NotImplementedError("Task 2")


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
