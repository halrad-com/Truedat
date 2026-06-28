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
import hashlib
import json
import logging
import lzma
import math
import os
import random
import re
import sqlite3
import sys
import tarfile
import time
import unicodedata
from collections import Counter, defaultdict
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
SEED_INDEX_PATH = DATA_DIR / "ab-seed-index.sqlite"
RECORDINGS_CACHE = DATA_DIR / ".mb-recordings-cache.pickle"

# AcousticBrainz sample dump (100k full Essentia JSON documents, 2 GB)
AB_SAMPLE_URL = (
    "https://data.metabrainz.org/pub/musicbrainz/acousticbrainz/dumps/"
    "acousticbrainz-sample-json-20220623/"
    "acousticbrainz-lowlevel-sample-json-20220623-0.tar.zst"
)

# MusicBrainz JSON dumps. The server rotates dumps and deletes old ones, so a
# hardcoded date goes stale (404). The default resolves the live LATEST pointer;
# MB_FALLBACK_DATE is only used if that fetch fails. Override with --mb-date.
MB_JSON_DUMPS_BASE = "https://data.metabrainz.org/pub/musicbrainz/data/json-dumps"
MB_FALLBACK_DATE = "20260627-001001"

# Download infrastructure constants
MANIFEST_PATH = DATA_DIR / ".download-manifest.json"
MAX_RETRIES = 3
RETRY_DELAYS = [5, 15, 45]
DOWNLOAD_CHUNK_SIZE = 1 << 16  # 64 KB
DOWNLOAD_TIMEOUT = 30


def _compute_sha256(path, chunk_size=1 << 20):
    """Compute SHA-256 hash of a file by streaming in chunks.

    Args:
        path: Path to the file to hash.
        chunk_size: Read buffer size in bytes (default: 1 MB).

    Returns:
        Hex-encoded SHA-256 digest string.
    """
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            block = f.read(chunk_size)
            if not block:
                break
            h.update(block)
    return h.hexdigest()


def _load_manifest():
    """Load the download manifest from disk.

    Returns:
        Dict mapping file paths (relative to DATA_DIR) to their recorded
        metadata (sha256, size). Returns an empty dict if the manifest
        is missing, empty, or corrupt.
    """
    if not MANIFEST_PATH.exists():
        return {}
    try:
        text = MANIFEST_PATH.read_text(encoding="utf-8")
        data = json.loads(text)
        if not isinstance(data, dict):
            log.warning("Manifest is not a dict, ignoring: %s", MANIFEST_PATH)
            return {}
        return data
    except (json.JSONDecodeError, OSError) as exc:
        log.warning("Manifest corrupt or unreadable, starting fresh: %s", exc)
        return {}


def _save_manifest(manifest):
    """Atomically save the download manifest (write to temp, then rename).

    Args:
        manifest: Dict to serialize as JSON.
    """
    MANIFEST_PATH.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = MANIFEST_PATH.with_suffix(".json.tmp")
    tmp_path.write_text(
        json.dumps(manifest, indent=2, sort_keys=True),
        encoding="utf-8",
    )
    os.replace(tmp_path, MANIFEST_PATH)


def _verify_download(dest, manifest):
    """Check whether a downloaded file matches its manifest entry.

    Verifies both file size and SHA-256 hash. Returns True only if the
    file exists on disk, appears in the manifest, and both size and hash
    match the recorded values.

    Args:
        dest: Path to the downloaded file.
        manifest: Current manifest dict.

    Returns:
        True if the file is verified intact, False otherwise.
    """
    if not dest.exists():
        return False

    key = dest.relative_to(DATA_DIR).as_posix()
    entry = manifest.get(key)
    if not entry:
        return False

    local_size = dest.stat().st_size
    if local_size != entry.get("size"):
        log.info("Size mismatch for %s: local=%d, manifest=%d",
                 dest.name, local_size, entry.get("size", -1))
        return False

    local_hash = _compute_sha256(dest)
    if local_hash != entry.get("sha256"):
        log.warning("Hash mismatch for %s — will re-download", dest.name)
        return False

    return True


def _record_download(url, dest, manifest):
    """Compute hash of a downloaded file and record it in the manifest.

    Args:
        url: Source URL the file was downloaded from.
        dest: Path to the downloaded file.
        manifest: Manifest dict to update (mutated in place).
    """
    key = dest.relative_to(DATA_DIR).as_posix()
    sha256 = _compute_sha256(dest)
    manifest[key] = {
        "url": url,
        "filename": dest.name,
        "sha256": sha256,
        "size": dest.stat().st_size,
        "downloaded_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }
    _save_manifest(manifest)
    log.info("Recorded in manifest: %s (%s)", dest.name, sha256[:12])


def _resolve_mb_date(mb_date):
    """Resolve the MusicBrainz dump date. 'latest' (the default) fetches the
    server's LATEST pointer file, whose contents are the current dump folder
    name (e.g. '20260627-001001') to append to the dumps URL. Old dumps get
    rotated off the server so a hardcoded date goes stale; LATEST never does.
    Falls back to MB_FALLBACK_DATE if the fetch fails. An explicit date string
    is returned unchanged."""
    if mb_date and mb_date.lower() != "latest":
        return mb_date
    url = f"{MB_JSON_DUMPS_BASE}/LATEST"
    try:
        resp = requests.get(url, timeout=(DOWNLOAD_TIMEOUT, DOWNLOAD_TIMEOUT))
        resp.raise_for_status()
        latest = resp.text.strip().splitlines()[0].strip().strip("/")
        if not latest:
            raise ValueError("LATEST pointer file was empty")
        log.info("Resolved latest MusicBrainz dump via LATEST: %s", latest)
        return latest
    except Exception as e:
        log.warning("Could not fetch %s (%s); falling back to %s",
                    url, e, MB_FALLBACK_DATE)
        return MB_FALLBACK_DATE


def _mb_dump_urls(mb_date):
    """Build MusicBrainz dump URLs for the given date string."""
    base = f"{MB_JSON_DUMPS_BASE}/{mb_date}"
    return {
        "release": f"{base}/release.tar.xz",
        "artist": f"{base}/artist.tar.xz",
        "release-group": f"{base}/release-group.tar.xz",
    }


def download_file(url, dest, description=None):
    """Download a file with resume support, progress bar, and retry.

    Retries up to MAX_RETRIES times with exponential backoff on failure.
    Supports HTTP Range headers for resuming partial downloads.

    Args:
        url: URL to download from.
        dest: Local path to save the file.
        description: Label for the progress bar (defaults to filename).

    Raises:
        requests.RequestException: If all retry attempts are exhausted.
    """
    dest.parent.mkdir(parents=True, exist_ok=True)
    label = description or dest.name

    for attempt in range(MAX_RETRIES + 1):
        try:
            _download_file_once(url, dest, label)
            return
        except requests.RequestException as exc:
            if attempt < MAX_RETRIES:
                delay = RETRY_DELAYS[min(attempt, len(RETRY_DELAYS) - 1)]
                log.warning(
                    "Attempt %d/%d failed for %s: %s — retrying in %ds",
                    attempt + 1, MAX_RETRIES + 1, label, exc, delay,
                )
                time.sleep(delay)
            else:
                log.error(
                    "All %d attempts failed for %s: %s",
                    MAX_RETRIES + 1, label, exc,
                )
                raise


def _download_file_once(url, dest, label):
    """Execute a single download attempt with resume support and progress bar.

    Args:
        url: URL to download from.
        dest: Local path to save the file.
        label: Display label for logging and progress bar.

    Raises:
        requests.RequestException: On any HTTP or connection error.
    """
    # HEAD is used for progress display and skip-if-complete heuristic.
    # The SHA-256 manifest check in download_all() is the authoritative integrity gate.
    head = requests.head(url, timeout=(DOWNLOAD_TIMEOUT, DOWNLOAD_TIMEOUT),
                         allow_redirects=True)
    head.raise_for_status()
    remote_size = int(head.headers.get("content-length", 0))

    if dest.exists():
        local_size = dest.stat().st_size
        if remote_size and local_size >= remote_size:
            log.info("Already downloaded: %s (%d bytes)", label, local_size)
            return
    else:
        local_size = 0

    headers = {}
    if local_size > 0:
        headers["Range"] = f"bytes={local_size}-"
        log.info("Resuming %s from %d bytes", label, local_size)
    else:
        log.info("Downloading %s (%s)", label,
                 f"{remote_size:,} bytes" if remote_size else "unknown size")

    # Use tuple timeout: (connect_timeout, read_timeout) to bound both
    # phases. A scalar only bounds the connect; read can hang indefinitely.
    resp = requests.get(
        url, headers=headers, stream=True,
        timeout=(DOWNLOAD_TIMEOUT, DOWNLOAD_TIMEOUT),
    )
    resp.raise_for_status()

    if resp.status_code == 206:
        total = local_size + int(resp.headers.get("content-length", 0))
    else:
        total = int(resp.headers.get("content-length", 0))
        local_size = 0

    mode = "ab" if resp.status_code == 206 else "wb"

    # Use try/finally to ensure response stream is closed on any error,
    # preventing leaked sockets in CLOSE_WAIT during multi-GB downloads.
    try:
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
            for chunk in resp.iter_content(chunk_size=DOWNLOAD_CHUNK_SIZE):
                if chunk:
                    f.write(chunk)
                    bar.update(len(chunk))
    finally:
        resp.close()

    final_size = dest.stat().st_size
    log.info("Completed: %s (%d bytes)", label, final_size)


def download_all(mb_date=MB_FALLBACK_DATE):
    """Download all required data dumps with manifest-based integrity tracking.

    Loads the SHA-256 manifest, checks each file against it, skips verified
    downloads, and records new downloads after each success.
    """
    log.info("Download directory: %s", DATA_DIR)
    manifest = _load_manifest()

    # Build the list of (url, dest, label) for all downloads
    ab_filename = AB_SAMPLE_URL.rsplit("/", 1)[-1]
    downloads = [
        (AB_SAMPLE_URL, AB_DIR / ab_filename, "AcousticBrainz sample"),
    ]

    mb_dumps = _mb_dump_urls(mb_date)
    for name, url in mb_dumps.items():
        mb_filename = url.rsplit("/", 1)[-1]
        downloads.append((url, MB_DIR / mb_filename, f"MusicBrainz {name}"))

    for url, dest, label in downloads:
        if _verify_download(dest, manifest):
            log.info("Verified (skipping): %s", label)
            continue

        download_file(url, dest, description=label)
        _record_download(url, dest, manifest)


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
# The AB sample dump uses old Essentia format (pre-music_extractor 2.0):
#   - tonal.key_key / tonal.key_scale  (not tonal.key_edma.*)
#   - lowlevel.average_loudness        (not lowlevel.loudness_ebu128.integrated)
#   - lowlevel.spectral_flatness_db    missing entirely → optional
_AB_FEATURE_MAP = [
    ("bpm", "rhythm.bpm", None, 1),
    ("key", "tonal.key_edma.key", "tonal.key_key", None),
    ("mode", "tonal.key_edma.scale", "tonal.key_scale", None),
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
    ("mfcc0", "lowlevel.mfcc.mean", None, 4),  # first element of array (energy)
    ("mfcc1", "lowlevel.mfcc.mean", None, 4),  # second element of array (timbre/spectral slope)
]

# Features that can be absent (old AB format lacks spectral_flatness_db entirely).
# Missing optional features get a neutral default instead of rejecting the whole entry.
_OPTIONAL_FEATURES = {"spectralFlatness"}
_OPTIONAL_DEFAULTS = {"spectralFlatness": -20.0}  # mid-range for dB flatness

# Sane numeric ranges for feature validation — rejects NaN, Inf, and out-of-range values.
# Note: loudness range covers both old AB format (average_loudness: 0–1)
# and new Essentia format (loudness_ebu128.integrated: -70–0 LUFS).
# MoodEstimator rank-normalizes, so absolute scale doesn't matter.
FEATURE_RANGES = {
    "bpm": (20.0, 300.0),
    "loudness": (-70.0, 1.0),
    "spectralCentroid": (0.0, 22050.0),
    "spectralFlux": (0.0, 10.0),
    "danceability": (0.0, 3.0),
    "onsetRate": (0.0, 50.0),
    "zeroCrossingRate": (0.0, 1.0),
    "spectralRms": (0.0, 1.0),
    "spectralFlatness": (-100.0, 0.0),
    "dissonance": (0.0, 1.0),
    "pitchSalience": (0.0, 1.0),
    "chordsChangesRate": (0.0, 5.0),
    "mfcc0": (-1000.0, 1000.0),
    "mfcc1": (-500.0, 500.0),
}


_rejection_counts = Counter()


def extract_ab_features(json_obj):
    """Extract the 15 Essentia features that MBXHub's MoodEstimator uses.

    Args:
        json_obj: Parsed Essentia JSON document.

    Returns:
        Dict with camelCase keys matching mbxmoods.json format, or None
        if any required feature is missing. Rejection reasons are tracked
        in _rejection_counts for observability.
    """
    result = {}

    for out_key, primary, fallback, precision in _AB_FEATURE_MAP:
        value = nav_path(json_obj, primary)
        if value is None and fallback:
            value = nav_path(json_obj, fallback)

        if value is None:
            if out_key in _OPTIONAL_FEATURES:
                result[out_key] = _OPTIONAL_DEFAULTS[out_key]
                continue
            _rejection_counts[f"{out_key} missing"] += 1
            return None

        # Special case: mfcc0/mfcc1 are elements of the MFCC array
        if out_key == "mfcc0":
            if not isinstance(value, list) or len(value) == 0:
                _rejection_counts[f"{out_key} empty array"] += 1
                return None
            value = value[0]
        elif out_key == "mfcc1":
            if not isinstance(value, list) or len(value) < 2:
                _rejection_counts[f"{out_key} array too short"] += 1
                return None
            value = value[1]

        # String features (key, mode) — must not be empty
        if out_key in ("key", "mode"):
            if not isinstance(value, str) or value == "":
                _rejection_counts[f"{out_key} invalid"] += 1
                return None
            result[out_key] = value
            continue

        # Numeric features — must be a number
        if not isinstance(value, (int, float)):
            _rejection_counts[f"{out_key} not numeric"] += 1
            return None
        if precision is not None:
            value = round(value, precision)
        result[out_key] = value

    # Validate numeric features are within sane ranges (reject NaN, Inf, out-of-range)
    for key, (lo, hi) in FEATURE_RANGES.items():
        val = result.get(key)
        if val is None:
            continue
        if not isinstance(val, (int, float)) or math.isnan(val) or math.isinf(val):
            _rejection_counts[f"{key} nan/inf"] += 1
            return None
        if val < lo or val > hi:
            _rejection_counts[f"{key} out of range"] += 1
            return None

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

    try:
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
    except (zstd.ZstdError, tarfile.ReadError) as exc:
        log.error("Archive may be corrupt or incomplete: %s", exc)
        log.error("Delete %s and re-run with --download", archive_path)
        return {}

    log.info(
        "AcousticBrainz: %d features extracted, %d skipped",
        len(features),
        skipped,
    )
    if _rejection_counts:
        top_reasons = _rejection_counts.most_common(5)
        reasons_str = ", ".join(f"{reason} ({count:,})" for reason, count in top_reasons)
        log.info("Top rejection reasons: %s", reasons_str)
    return features


# ---------------------------------------------------------------------------
# MusicBrainz metadata extraction
# ---------------------------------------------------------------------------

_YEAR_RE = re.compile(r"(\d{4})")
_TRACK_NUM_RE = re.compile(r"(\d+)")


_NON_GENRE_TAGS = frozenset({
    "seen live", "favorites", "favourite", "favourites", "check out",
    "female vocalists", "male vocalists", "my collection", "albums i own",
    "under 2000 listeners", "beautiful", "awesome", "cool", "love",
    "spotify", "todo", "to listen", "want to hear", "wish list",
    "own it", "i own this", "vinyl", "cd", "lp",
})


def _extract_genre_from_tags(tags_list):
    """Return the highest-count genre tag name, title-cased, from a list of tag dicts.

    Filters out common MusicBrainz non-genre tags (personal tags, format tags, etc.)
    before selecting the highest-count tag.

    Args:
        tags_list: List of dicts like [{"name": "rock", "count": 15}, ...].

    Returns:
        Title-cased genre string, or "" if the list is empty/None or all tags
        are non-genre.
    """
    if not tags_list:
        return ""
    filtered = [t for t in tags_list if t.get("name", "").lower() not in _NON_GENRE_TAGS]
    if not filtered:
        return ""
    best = max(filtered, key=lambda t: t.get("count", 0))
    name = best.get("name", "")
    return name.title() if name else ""


def _extract_year(date_str):
    """Extract a 4-digit year from a date string.

    Args:
        date_str: Date string like "1975-10-31", "1975", or "".

    Returns:
        Year as int, or 0 if missing or unparseable.
    """
    if not date_str:
        return 0
    m = _YEAR_RE.search(str(date_str))
    return int(m.group(1)) if m else 0


def _parse_track_number(num_str):
    """Parse a track number string to int.

    Args:
        num_str: Track number string (could be "1", "A1", "").

    Returns:
        Integer track number, defaulting to 1 if unparseable.
    """
    if not num_str:
        return 1
    m = _TRACK_NUM_RE.search(str(num_str))
    return int(m.group(1)) if m else 1


def _load_release_group_genres():
    """Parse the release-group tar.xz dump to build release_group MBID → genre mapping.

    Returns:
        dict mapping release_group MBID (str) → genre (str).
    """
    archive_path = MB_DIR / "release-group.tar.xz"
    if not archive_path.exists():
        log.error("Release-group archive not found: %s", archive_path)
        log.error("Run with --download first.")
        return {}

    log.info("Loading release-group genres from %s", archive_path.name)

    rg_genres = {}

    try:
        with lzma.open(archive_path, "rb") as xz:
            tar = tarfile.open(fileobj=xz, mode="r|")

            for member in tar:
                if not member.isfile():
                    continue

                f = tar.extractfile(member)
                if f is None:
                    continue

                for line in f:
                    line = line.strip()
                    if not line:
                        continue

                    try:
                        rg = json.loads(line)
                    except (json.JSONDecodeError, ValueError):
                        continue

                    if not isinstance(rg, dict):
                        continue

                    rg_id = rg.get("id")
                    if not rg_id:
                        continue

                    # Try "genres" first (newer dumps), then "tags" as fallback
                    genre = _extract_genre_from_tags(rg.get("genres"))
                    if not genre:
                        genre = _extract_genre_from_tags(rg.get("tags"))

                    if genre:
                        rg_genres[rg_id] = genre

            tar.close()
    except (lzma.LZMAError, tarfile.ReadError) as exc:
        log.error("Archive may be corrupt or incomplete: %s", exc)
        log.error("Delete %s and re-run with --download", archive_path)
        return {}

    log.info("Release-group genres loaded: %d entries", len(rg_genres))
    return rg_genres


def _recordings_cache_valid():
    """Check if MB recordings cache exists and matches current release archive."""
    archive = MB_DIR / "release.tar.xz"
    if not RECORDINGS_CACHE.exists() or not archive.exists():
        return False
    try:
        import pickle
        with open(RECORDINGS_CACHE, "rb") as f:
            header = pickle.load(f)  # first object is metadata
        return (header.get("archive_size") == archive.stat().st_size and
                header.get("archive_mtime") == archive.stat().st_mtime)
    except Exception:
        return False


def _load_recordings_cache():
    """Load cached recordings from pickle file."""
    import pickle
    with open(RECORDINGS_CACHE, "rb") as f:
        _header = pickle.load(f)  # metadata header
        recordings = pickle.load(f)  # actual data
    return recordings


def _save_recordings_cache(recordings):
    """Save recordings to pickle cache with archive metadata."""
    import pickle
    archive = MB_DIR / "release.tar.xz"
    header = {
        "archive_size": archive.stat().st_size,
        "archive_mtime": archive.stat().st_mtime,
        "recording_count": len(recordings),
    }
    tmp = RECORDINGS_CACHE.with_suffix(".pickle.tmp")
    with open(tmp, "wb") as f:
        pickle.dump(header, f)
        pickle.dump(recordings, f)
    os.replace(tmp, RECORDINGS_CACHE)
    log.info("Saved recordings cache: %d entries", len(recordings))


def _normalize_text(text):
    """Normalize text for seeding lookup matching.

    Lowercase, strip accents (NFD decomposition + remove combining marks),
    strip punctuation, collapse whitespace.

    IMPORTANT: Must produce identical results to PathSanitizer.NormalizeForLookup()
    in C# for cross-language matching.

    Args:
        text: Input string.

    Returns:
        Normalized string.
    """
    if not text:
        return ""
    # NFD decompose, remove combining marks (accents)
    text = unicodedata.normalize("NFD", text)
    text = "".join(c for c in text if unicodedata.category(c) != "Mn")
    # Lowercase
    text = text.lower()
    # Strip non-ASCII-alphanumeric (keep a-z, 0-9, whitespace).
    # Using explicit ASCII range instead of \w to ensure identical behavior
    # between Python and C# (Unicode \w semantics differ between runtimes).
    text = re.sub(r"[^a-z0-9\s]", "", text)
    # Collapse whitespace
    text = re.sub(r"\s+", " ", text).strip()
    return text


def load_mb_metadata(ab_mbids=None):
    """Parse the MusicBrainz release dump to build recording → metadata mapping.

    Streams through the release tar.xz archive (17GB compressed) one line at a
    time to avoid loading the entire dump into memory.

    Args:
        ab_mbids: Optional set of recording MBIDs from AcousticBrainz. When
            provided, recordings with no genre AND no AB match are skipped
            to reduce memory usage.

    Returns:
        dict mapping recording MBID (str) → metadata dict with keys:
            title, artist, artistMbid, albumArtist, album, releaseMbid,
            releaseGroupMbid, genre, year, trackNo, totalTracks
    """
    archive_path = MB_DIR / "release.tar.xz"
    if not archive_path.exists():
        log.error("Release archive not found: %s", archive_path)
        log.error("Run with --download first.")
        return {}

    # Check cache first
    if _recordings_cache_valid():
        log.info("Loading MB recordings from cache...")
        recordings = _load_recordings_cache()
        log.info("Loaded %d recordings from cache", len(recordings))
        return recordings

    # Load release-group genres first
    rg_genres = _load_release_group_genres()

    log.info("Loading MusicBrainz release metadata from %s", archive_path.name)

    recordings = {}
    release_count = 0
    skipped = 0

    try:
        with lzma.open(archive_path, "rb") as xz:
            tar = tarfile.open(fileobj=xz, mode="r|")

            for member in tar:
                if not member.isfile():
                    continue

                f = tar.extractfile(member)
                if f is None:
                    continue

                for line in f:
                    line = line.strip()
                    if not line:
                        continue

                    try:
                        release = json.loads(line)
                    except (json.JSONDecodeError, ValueError):
                        skipped += 1
                        continue

                    if not isinstance(release, dict):
                        continue

                    release_count += 1
                    if release_count % 100_000 == 0:
                        log.info(
                            "Processed %dk releases, %d recordings so far",
                            release_count // 1000,
                            len(recordings),
                        )

                    # Extract release-level fields
                    release_mbid = release.get("id", "")
                    album = release.get("title", "")
                    year = _extract_year(release.get("date", ""))

                    # Artist credit — take first entry
                    artist_credit = release.get("artist-credit", [])
                    if not artist_credit:
                        skipped += 1
                        continue
                    first_credit = artist_credit[0]
                    artist_obj = first_credit.get("artist", {})
                    artist_name = artist_obj.get("name", "")
                    artist_mbid = artist_obj.get("id", "")

                    # Skip releases missing required fields
                    if not album or not artist_name or not year:
                        skipped += 1
                        continue

                    # Release group and genre
                    rg_obj = release.get("release-group", {}) or {}
                    rg_mbid = rg_obj.get("id", "")
                    genre = rg_genres.get(rg_mbid, "") if rg_mbid else ""

                    # Fallback: try genre/tags on the release itself
                    if not genre:
                        genre = _extract_genre_from_tags(release.get("genres"))
                    if not genre:
                        genre = _extract_genre_from_tags(release.get("tags"))

                    # Process each medium and track
                    for medium in release.get("media", []):
                        total_tracks = medium.get("track-count", 0)

                        for track in medium.get("tracks", []):
                            recording = track.get("recording", {}) or {}
                            recording_mbid = recording.get("id", "")
                            if not recording_mbid:
                                continue

                            # Skip recordings already seen (keep first occurrence)
                            if recording_mbid in recordings:
                                continue

                            # Memory optimization: skip recordings with no genre
                            # and no AB match — they're useless to the catalog.
                            if ab_mbids is not None:
                                if not genre and recording_mbid not in ab_mbids:
                                    continue

                            title = track.get("title") or recording.get("title", "")
                            track_no = _parse_track_number(track.get("number", ""))

                            recordings[recording_mbid] = {
                                "title": title,
                                "artist": artist_name,
                                "artistMbid": artist_mbid,
                                "albumArtist": artist_name,
                                "album": album,
                                "releaseMbid": release_mbid,
                                "releaseGroupMbid": rg_mbid,
                                "genre": genre,
                                "year": year,
                                "trackNo": track_no,
                                "totalTracks": total_tracks,
                                "normalizedArtist": _normalize_text(artist_name),
                                "normalizedTitle": _normalize_text(title),
                            }

            tar.close()
    except (lzma.LZMAError, tarfile.ReadError) as exc:
        log.error("Archive may be corrupt or incomplete: %s", exc)
        log.error("Delete %s and re-run with --download", archive_path)
        return {}

    log.info(
        "MusicBrainz: %d recordings from %d releases (%d skipped)",
        len(recordings),
        release_count,
        skipped,
    )
    _save_recordings_cache(recordings)
    return recordings


def write_catalog(entries, path=CATALOG_PATH):
    """Write catalog entries to a gzipped JSONL file (atomic).

    Writes to a .tmp file first, then atomically replaces the target —
    mirrors the AtomicReplace pattern from Program.cs.

    Args:
        entries: List of catalog entry dicts to write.
        path: Output path (default: CATALOG_PATH).
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = path.with_suffix(path.suffix + ".tmp")

    with gzip.open(tmp_path, "wt", encoding="utf-8") as f:
        for entry in entries:
            f.write(json.dumps(entry, ensure_ascii=False))
            f.write("\n")

    os.replace(tmp_path, path)

    size_mb = path.stat().st_size / (1024 * 1024)
    log.info("Wrote %d entries to %s (%.1f MB)", len(entries), path.name, size_mb)


def _write_seed_index(entries):
    """Write SQLite seed index for fast seeding lookup.

    Creates two tables: features (keyed by recording MBID with all 15 acoustic
    features) and recordings (keyed by MBID with normalized artist+title for
    metadata matching).

    Atomic: writes to .tmp, renames on success.

    Args:
        entries: List of catalog entry dicts.
    """
    SEED_INDEX_PATH.parent.mkdir(parents=True, exist_ok=True)
    tmp = SEED_INDEX_PATH.with_suffix(".sqlite.tmp")

    if tmp.exists():
        tmp.unlink()

    log.info("Building seed index: %s", SEED_INDEX_PATH.name)

    conn = sqlite3.connect(str(tmp))
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA synchronous=NORMAL")

    conn.executescript("""
        CREATE TABLE features (
            recording_mbid TEXT PRIMARY KEY,
            bpm REAL, key TEXT, mode TEXT,
            loudness REAL, spectral_centroid REAL, spectral_flux REAL,
            danceability REAL, onset_rate REAL, zero_crossing_rate REAL,
            spectral_rms REAL, spectral_flatness REAL,
            dissonance REAL, pitch_salience REAL, chords_changes_rate REAL,
            mfcc0 REAL, mfcc1 REAL, genre TEXT
        );

        CREATE TABLE recordings (
            recording_mbid TEXT PRIMARY KEY,
            normalized_artist TEXT NOT NULL,
            normalized_title TEXT NOT NULL,
            artist TEXT, title TEXT, album TEXT,
            year INTEGER, artist_mbid TEXT, release_mbid TEXT
        );

        CREATE INDEX idx_recordings_artist_title
            ON recordings(normalized_artist, normalized_title);

        CREATE INDEX idx_recordings_artist_mbid
            ON recordings(artist_mbid);
    """)

    features_batch = []
    recordings_batch = []

    for entry in entries:
        mbid = entry.get("mbid", "")
        features_batch.append((
            mbid,
            entry.get("bpm"), entry.get("key"), entry.get("mode"),
            entry.get("loudness"), entry.get("spectralCentroid"),
            entry.get("spectralFlux"), entry.get("danceability"),
            entry.get("onsetRate"), entry.get("zeroCrossingRate"),
            entry.get("spectralRms"), entry.get("spectralFlatness"),
            entry.get("dissonance"), entry.get("pitchSalience"),
            entry.get("chordsChangesRate"), entry.get("mfcc0"), entry.get("mfcc1"),
            entry.get("genre"),
        ))
        recordings_batch.append((
            mbid,
            entry.get("normalizedArtist", ""),
            entry.get("normalizedTitle", ""),
            entry.get("artist", ""),
            entry.get("title", ""),
            entry.get("album", ""),
            entry.get("year", 0),
            entry.get("artistMbid", ""),
            entry.get("releaseMbid", ""),
        ))

    conn.executemany(
        "INSERT OR IGNORE INTO features VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        features_batch
    )
    conn.executemany(
        "INSERT OR IGNORE INTO recordings VALUES (?,?,?,?,?,?,?,?,?)",
        recordings_batch
    )
    conn.commit()

    feat_count = conn.execute("SELECT COUNT(*) FROM features").fetchone()[0]
    rec_count = conn.execute("SELECT COUNT(*) FROM recordings").fetchone()[0]
    conn.close()

    # Atomic rename
    if SEED_INDEX_PATH.exists():
        SEED_INDEX_PATH.unlink()
    tmp.rename(SEED_INDEX_PATH)

    size_mb = SEED_INDEX_PATH.stat().st_size / (1024 * 1024)
    log.info("Seed index: %s (%.1f MB, %d features, %d recordings)",
             SEED_INDEX_PATH.name, size_mb, feat_count, rec_count)


def build_catalog(target=430_000):
    """Build the synthetic library catalog.

    Joins AcousticBrainz features with MusicBrainz metadata by recording MBID,
    then extends the catalog to the target size by assigning genre-matched
    feature sets from the AB pool to unmatched MB recordings.

    Args:
        target: Target number of catalog entries (default: 430,000).
    """
    # 1. Load both datasets (AB first so we can filter MB by matched MBIDs)
    ab_features = load_ab_features()
    mb_metadata = load_mb_metadata(ab_mbids=set(ab_features.keys()) if ab_features else None)

    if not ab_features or not mb_metadata:
        log.error("Cannot build catalog — missing data. Run with --download first.")
        return

    # 2. Join — match AB recording MBIDs to MB metadata
    matched = []
    for mbid, features in ab_features.items():
        meta = mb_metadata.get(mbid)
        if meta is None:
            continue
        # Require genre to be non-empty
        if not meta.get("genre"):
            continue
        entry = {**meta, **features, "mbid": mbid}
        matched.append(entry)

    log.info("Matched %d recordings (AB features + MB metadata with genre)", len(matched))

    # 3. Build per-genre feature pools from matched entries
    feature_keys = [key for key, _, _, _ in _AB_FEATURE_MAP]
    genre_pools = defaultdict(list)

    for entry in matched:
        genre = entry["genre"]
        pool_entry = {k: entry[k] for k in feature_keys}
        genre_pools[genre].append(pool_entry)

    # Log top 10 genres by count
    top_genres = sorted(genre_pools.items(), key=lambda x: len(x[1]), reverse=True)[:10]
    log.info("Top 10 genres in feature pool:")
    for genre, pool in top_genres:
        log.info("  %-25s %d", genre, len(pool))

    # Build a flat list of all pool entries for fallback
    all_pool_entries = []
    for pool in genre_pools.values():
        all_pool_entries.extend(pool)

    # 4. Extend catalog to target size
    entries = list(matched)
    needed = target - len(entries)

    if needed <= 0:
        log.info("Matched entries (%d) already meet target (%d)", len(entries), target)
    else:
        log.info("Need %d more entries to reach target %d", needed, target)

        # Collect unmatched MB recordings that have genre but no AB features
        matched_mbids = set(ab_features.keys())
        unmatched = []
        for mbid, meta in mb_metadata.items():
            if mbid in matched_mbids:
                continue
            if not meta.get("genre"):
                continue
            unmatched.append((mbid, meta))

        log.info("Unmatched MB recordings with genre: %d", len(unmatched))

        # Shuffle with fixed seed for reproducibility
        rng = random.Random(42)
        rng.shuffle(unmatched)

        # Assign features from same-genre pool (or any pool as fallback)
        added = 0
        for mbid, meta in unmatched:
            if added >= needed:
                break

            genre = meta["genre"]
            pool = genre_pools.get(genre, all_pool_entries)
            if not pool:
                pool = all_pool_entries

            feature_set = rng.choice(pool)
            entry = {**meta, **feature_set, "mbid": mbid}
            entries.append(entry)
            added += 1

            if added % 50_000 == 0:
                log.info("  Extended: %dk / %dk", added // 1000, needed // 1000)

        log.info("Extended catalog by %d entries (total: %d)", added, len(entries))

    # 5. Write catalog
    write_catalog(entries)
    _write_seed_index(entries)


def show_stats():
    """Show statistics about an existing catalog."""
    if not CATALOG_PATH.exists():
        log.error("Catalog not found: %s", CATALOG_PATH)
        log.error("Run with --build first.")
        return

    total = 0
    artists = set()
    albums = set()  # (artist, album) pairs
    genre_counts = defaultdict(int)
    decade_counts = defaultdict(int)

    with gzip.open(CATALOG_PATH, "rt", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            entry = json.loads(line)
            total += 1

            artist = entry.get("artist", "")
            album = entry.get("album", "")
            genre = entry.get("genre", "")
            year = entry.get("year", 0)

            if artist:
                artists.add(artist)
            if artist and album:
                albums.add((artist, album))
            if genre:
                genre_counts[genre] += 1
            if year:
                decade = (year // 10) * 10
                decade_counts[decade] += 1

    # Print report
    print(f"\n{'=' * 60}")
    print(f"  Catalog: {CATALOG_PATH.name}")
    print(f"  Size:    {CATALOG_PATH.stat().st_size / (1024 * 1024):.1f} MB")
    print(f"{'=' * 60}")
    print(f"  Total entries:   {total:>10,}")
    print(f"  Unique artists:  {len(artists):>10,}")
    print(f"  Unique albums:   {len(albums):>10,}")
    print()

    # Top 20 genres
    print(f"  {'Genre':<30} {'Count':>10} {'%':>7}")
    print(f"  {'-' * 30} {'-' * 10} {'-' * 7}")
    top_genres = sorted(genre_counts.items(), key=lambda x: x[1], reverse=True)[:20]
    for genre, count in top_genres:
        pct = count / total * 100 if total else 0
        print(f"  {genre:<30} {count:>10,} {pct:>6.1f}%")
    print()

    # Decade distribution
    print(f"  {'Decade':<30} {'Count':>10} {'%':>7}")
    print(f"  {'-' * 30} {'-' * 10} {'-' * 7}")
    for decade in sorted(decade_counts.keys()):
        count = decade_counts[decade]
        pct = count / total * 100 if total else 0
        print(f"  {decade}s{'':<26} {count:>10,} {pct:>6.1f}%")
    print()

    # Seed index stats
    if SEED_INDEX_PATH.exists():
        conn = sqlite3.connect(str(SEED_INDEX_PATH))
        feat_count = conn.execute("SELECT COUNT(*) FROM features").fetchone()[0]
        rec_count = conn.execute("SELECT COUNT(*) FROM recordings").fetchone()[0]
        conn.close()
        size_mb = SEED_INDEX_PATH.stat().st_size / (1024 * 1024)
        print(f"  Seed Index: {SEED_INDEX_PATH.name} ({size_mb:.1f} MB)")
        print(f"  Features:  {feat_count:>10,}")
        print(f"  Recordings: {rec_count:>10,}")
    else:
        print(f"\n  Seed index not found: {SEED_INDEX_PATH}")


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
    parser.add_argument(
        "--target", type=int, default=430_000,
        help="Target catalog size (default: 430000)"
    )
    parser.add_argument(
        "--mb-date", default="latest",
        help="MusicBrainz dump date, e.g. 20260627-001001, or 'latest' to "
             "auto-resolve the newest dump from the server's LATEST pointer "
             "(default: latest)"
    )
    args = parser.parse_args()

    if not (args.download or args.build or args.stats):
        parser.print_help()
        return 1

    if args.build and args.target <= 0:
        log.error("--target must be > 0, got %d", args.target)
        return 1

    if args.download:
        args.mb_date = _resolve_mb_date(args.mb_date)
    log.info("MusicBrainz dump date: %s", args.mb_date)

    if args.download:
        download_all(mb_date=args.mb_date)
    if args.build:
        build_catalog(target=args.target)
    if args.stats:
        show_stats()
    return 0


if __name__ == "__main__":
    sys.exit(main())
