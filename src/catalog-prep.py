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
import lzma
import os
import random
import re
import sys
import tarfile
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

# AcousticBrainz sample dump (100k full Essentia JSON documents, 2 GB)
AB_SAMPLE_URL = (
    "https://data.metabrainz.org/pub/musicbrainz/acousticbrainz/dumps/"
    "acousticbrainz-sample-json-20220623/"
    "acousticbrainz-lowlevel-sample-json-20220623-0.tar.zst"
)

# MusicBrainz JSON dumps — date set via --mb-date CLI arg
MB_DEFAULT_DATE = "20260225-001001"


def _mb_dump_urls(mb_date):
    """Build MusicBrainz dump URLs for the given date string."""
    base = f"https://data.metabrainz.org/pub/musicbrainz/data/json-dumps/{mb_date}"
    return {
        "release": f"{base}/release.tar.xz",
        "artist": f"{base}/artist.tar.xz",
        "release-group": f"{base}/release-group.tar.xz",
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


def download_all(mb_date=MB_DEFAULT_DATE):
    """Download all required data dumps."""
    log.info("Download directory: %s", DATA_DIR)

    # AcousticBrainz sample archive
    ab_filename = AB_SAMPLE_URL.rsplit("/", 1)[-1]
    ab_dest = AB_DIR / ab_filename
    download_file(AB_SAMPLE_URL, ab_dest, description="AcousticBrainz sample")

    # MusicBrainz JSON dumps
    mb_dumps = _mb_dump_urls(mb_date)
    for name, url in mb_dumps.items():
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
            _rejection_counts[f"{out_key} missing"] += 1
            return None

        # Special case: mfcc0 is the first element of the MFCC array
        if out_key == "mfcc0":
            if not isinstance(value, list) or len(value) == 0:
                _rejection_counts[f"{out_key} empty array"] += 1
                return None
            value = value[0]

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
        "--mb-date", default=MB_DEFAULT_DATE,
        help=f"MusicBrainz dump date (default: {MB_DEFAULT_DATE})"
    )
    args = parser.parse_args()

    if not (args.download or args.build or args.stats):
        parser.print_help()
        return 1

    if args.build and args.target <= 0:
        log.error("--target must be > 0, got %d", args.target)
        return 1

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
