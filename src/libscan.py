#!/usr/bin/env python3
"""Scan a music library directory and report stats.

Usage:
    python libscan.py <path> [--tags] [--deep]

    <path>          Root directory to scan
    --tags          Read ID3/metadata tags (slower, requires mutagen)
    --deep          Include per-genre and per-artist breakdowns
"""

import argparse
import os
import sys
from collections import Counter, defaultdict
from pathlib import Path

# Audio file extensions we care about
AUDIO_EXTENSIONS = {
    ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".wav", ".asf",
    ".aiff", ".aif", ".alac", ".ape", ".dsf", ".dff", ".wv", ".mpc",
}

IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"}
PLAYLIST_EXTENSIONS = {".m3u", ".m3u8", ".pls", ".xspf"}


def format_size(nbytes):
    """Human-readable file size."""
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if abs(nbytes) < 1024:
            return f"{nbytes:.1f} {unit}"
        nbytes /= 1024
    return f"{nbytes:.1f} PB"


def scan_files(root):
    """Walk directory tree and classify files."""
    audio_files = []
    other_files = Counter()  # extension -> count
    other_sizes = Counter()  # extension -> total bytes
    total_dirs = 0
    errors = []

    for dirpath, dirnames, filenames in os.walk(root):
        total_dirs += 1
        for fname in filenames:
            fpath = os.path.join(dirpath, fname)
            ext = os.path.splitext(fname)[1].lower()

            try:
                size = os.path.getsize(fpath)
            except OSError as e:
                errors.append((fpath, str(e)))
                continue

            if ext in AUDIO_EXTENSIONS:
                # Depth from root (for structure analysis)
                rel = os.path.relpath(dirpath, root)
                depth = 0 if rel == "." else rel.count(os.sep) + 1
                audio_files.append({
                    "path": fpath,
                    "name": fname,
                    "ext": ext,
                    "size": size,
                    "dir": dirpath,
                    "depth": depth,
                    "rel_dir": rel,
                })
            else:
                other_files[ext] += 1
                other_sizes[ext] += size

    return audio_files, other_files, other_sizes, total_dirs, errors


def read_tags(audio_files):
    """Read ID3/metadata tags using mutagen. Returns enriched file list."""
    try:
        import mutagen
    except ImportError:
        print("Warning: mutagen not installed. Skipping tag reading.")
        print("  Install with: pip install mutagen")
        return audio_files

    tag_stats = {
        "has_artist": 0,
        "has_title": 0,
        "has_album": 0,
        "has_genre": 0,
        "has_year": 0,
        "has_bpm": 0,
        "missing_artist": 0,
        "missing_title": 0,
        "tag_errors": 0,
    }
    genres = Counter()
    artists = Counter()
    years = Counter()
    bitrates = []

    for i, f in enumerate(audio_files):
        if (i + 1) % 10000 == 0:
            print(f"  Reading tags: {i + 1:,}/{len(audio_files):,}", flush=True)

        try:
            m = mutagen.File(f["path"], easy=True)
            if m is None:
                tag_stats["tag_errors"] += 1
                continue
        except Exception:
            tag_stats["tag_errors"] += 1
            continue

        artist = (m.get("artist") or [None])[0]
        title = (m.get("title") or [None])[0]
        album = (m.get("album") or [None])[0]
        genre = (m.get("genre") or [None])[0]
        date = (m.get("date") or [None])[0]
        bpm = (m.get("bpm") or [None])[0]

        if artist:
            tag_stats["has_artist"] += 1
            artists[artist] += 1
        else:
            tag_stats["missing_artist"] += 1

        if title:
            tag_stats["has_title"] += 1
        else:
            tag_stats["missing_title"] += 1

        if album:
            tag_stats["has_album"] += 1
        if genre:
            tag_stats["has_genre"] += 1
            genres[genre] += 1
        if date:
            tag_stats["has_year"] += 1
            # Extract just the year
            year = date[:4] if len(date) >= 4 else date
            years[year] += 1
        if bpm:
            tag_stats["has_bpm"] += 1

        # Bitrate (from mutagen.info, not easy tags)
        try:
            m2 = mutagen.File(f["path"])
            if m2 and hasattr(m2.info, "bitrate") and m2.info.bitrate:
                bitrates.append(m2.info.bitrate // 1000)
        except Exception:
            pass

        f["artist"] = artist
        f["genre"] = genre
        f["year"] = date

    return {
        "stats": tag_stats,
        "genres": genres,
        "artists": artists,
        "years": years,
        "bitrates": bitrates,
    }


def print_report(audio_files, other_files, other_sizes, total_dirs, errors,
                 tag_data=None, deep=False):
    """Print the scan report."""
    print()
    print("=" * 60)
    print("  Library Scan Report")
    print("=" * 60)
    print()

    # --- Overview ---
    total_audio = len(audio_files)
    total_audio_size = sum(f["size"] for f in audio_files)
    total_other_count = sum(other_files.values())
    total_other_size = sum(other_sizes.values())

    print(f"  Audio files:    {total_audio:>10,}")
    print(f"  Audio size:     {format_size(total_audio_size):>10}")
    print(f"  Other files:    {total_other_count:>10,}")
    print(f"  Other size:     {format_size(total_other_size):>10}")
    print(f"  Directories:    {total_dirs:>10,}")
    if errors:
        print(f"  Read errors:    {len(errors):>10,}")
    print()

    # --- Audio by format ---
    print("  Audio Files by Format")
    print("  " + "-" * 45)
    ext_counts = Counter(f["ext"] for f in audio_files)
    ext_sizes = defaultdict(int)
    for f in audio_files:
        ext_sizes[f["ext"]] += f["size"]

    for ext, count in ext_counts.most_common():
        pct = count / total_audio * 100 if total_audio else 0
        print(f"    {ext:8s}  {count:>10,}  ({pct:5.1f}%)  {format_size(ext_sizes[ext]):>10}")
    print()

    # --- Size distribution ---
    print("  File Size Distribution")
    print("  " + "-" * 45)
    size_buckets = [
        ("< 100 KB", 0, 100 * 1024),
        ("100 KB - 1 MB", 100 * 1024, 1024 * 1024),
        ("1 MB - 5 MB", 1024 * 1024, 5 * 1024 * 1024),
        ("5 MB - 10 MB", 5 * 1024 * 1024, 10 * 1024 * 1024),
        ("10 MB - 50 MB", 10 * 1024 * 1024, 50 * 1024 * 1024),
        ("50 MB+", 50 * 1024 * 1024, float("inf")),
    ]
    for label, lo, hi in size_buckets:
        count = sum(1 for f in audio_files if lo <= f["size"] < hi)
        if count > 0:
            pct = count / total_audio * 100
            print(f"    {label:16s}  {count:>10,}  ({pct:5.1f}%)")
    print()

    if total_audio > 0:
        sizes = sorted(f["size"] for f in audio_files)
        avg = total_audio_size / total_audio
        median = sizes[total_audio // 2]
        print(f"  Avg size:   {format_size(avg)}")
        print(f"  Median:     {format_size(median)}")
        print(f"  Smallest:   {format_size(sizes[0])}")
        print(f"  Largest:    {format_size(sizes[-1])}")
        print()

    # --- Directory structure ---
    print("  Directory Depth (folders from root to file)")
    print("  " + "-" * 55)
    depth_counts = Counter(f["depth"] for f in audio_files)
    depth_examples = {}
    for f in audio_files:
        d = f["depth"]
        if d not in depth_examples:
            # Show the folder structure pattern (not the full path)
            parts = f["rel_dir"].split(os.sep)
            depth_examples[d] = os.sep.join(parts[:d]) + os.sep + "track"

    for depth in sorted(depth_counts):
        count = depth_counts[depth]
        pct = count / total_audio * 100 if total_audio else 0
        example = depth_examples.get(depth, "")
        print(f"    {depth}  {count:>10,}  ({pct:5.1f}%)  e.g. {example}")
    print()

    # Top-level folders
    top_folders = Counter()
    for f in audio_files:
        parts = f["rel_dir"].split(os.sep)
        if parts and parts[0] != ".":
            top_folders[parts[0]] += 1
        else:
            top_folders["(root)"] += 1

    print(f"  Top-Level Folders ({len(top_folders)} total)")
    print("  " + "-" * 45)
    for folder, count in top_folders.most_common(20):
        pct = count / total_audio * 100 if total_audio else 0
        print(f"    {folder[:30]:30s}  {count:>10,}  ({pct:5.1f}%)")
    if len(top_folders) > 20:
        print(f"    ... and {len(top_folders) - 20} more")
    print()

    # --- Other files ---
    if other_files:
        print("  Non-Audio Files")
        print("  " + "-" * 45)
        for ext, count in other_files.most_common(15):
            ext_label = ext if ext else "(no ext)"
            print(f"    {ext_label:8s}  {count:>10,}  {format_size(other_sizes[ext]):>10}")
        if len(other_files) > 15:
            print(f"    ... and {len(other_files) - 15} more types")
        print()

    # --- Tag data ---
    if tag_data:
        ts = tag_data["stats"]
        total = total_audio
        print("  Tag Coverage")
        print("  " + "-" * 45)
        for field in ("artist", "title", "album", "genre", "year", "bpm"):
            has = ts.get(f"has_{field}", 0)
            pct = has / total * 100 if total else 0
            print(f"    {field:10s}  {has:>10,}  ({pct:5.1f}%)")
        if ts["tag_errors"]:
            print(f"    {'errors':10s}  {ts['tag_errors']:>10,}")
        if ts["missing_artist"]:
            print(f"\n  Missing artist: {ts['missing_artist']:,}")
        if ts["missing_title"]:
            print(f"  Missing title:  {ts['missing_title']:,}")
        print()

        # Bitrate distribution
        if tag_data["bitrates"]:
            br = tag_data["bitrates"]
            print("  Bitrate Distribution (kbps)")
            print("  " + "-" * 45)
            br_buckets = [
                ("< 128", 0, 128),
                ("128", 128, 160),
                ("160-192", 160, 224),
                ("224-256", 224, 288),
                ("320 (CBR)", 288, 330),
                ("VBR/High", 330, 10000),
            ]
            for label, lo, hi in br_buckets:
                count = sum(1 for b in br if lo <= b < hi)
                if count > 0:
                    pct = count / len(br) * 100
                    print(f"    {label:16s}  {count:>10,}  ({pct:5.1f}%)")
            print()

        # Top genres
        if tag_data["genres"] and deep:
            print(f"  Top Genres ({len(tag_data['genres'])} unique)")
            print("  " + "-" * 45)
            for genre, count in tag_data["genres"].most_common(25):
                pct = count / total * 100
                print(f"    {genre[:30]:30s}  {count:>10,}  ({pct:5.1f}%)")
            print()

        # Year distribution
        if tag_data["years"] and deep:
            print("  Decade Distribution")
            print("  " + "-" * 45)
            decades = Counter()
            for year, count in tag_data["years"].items():
                try:
                    decade = (int(year) // 10) * 10
                    decades[decade] += count
                except (ValueError, TypeError):
                    decades[0] += count
            for decade in sorted(decades):
                count = decades[decade]
                pct = count / total * 100
                label = f"{decade}s" if decade > 0 else "Unknown"
                print(f"    {label:16s}  {count:>10,}  ({pct:5.1f}%)")
            print()

        # Top artists
        if tag_data["artists"] and deep:
            print(f"  Top Artists ({len(tag_data['artists'])} unique)")
            print("  " + "-" * 45)
            for artist, count in tag_data["artists"].most_common(25):
                print(f"    {artist[:30]:30s}  {count:>10,}")
            print()

    # --- Errors ---
    if errors:
        print("  Read Errors")
        print("  " + "-" * 45)
        for path, err in errors[:10]:
            print(f"    {path}")
            print(f"      {err}")
        if len(errors) > 10:
            print(f"    ... and {len(errors) - 10} more")
        print()

    print("=" * 60)


def main():
    parser = argparse.ArgumentParser(description="Scan a music library and report stats.")
    parser.add_argument("path", help="Root directory to scan")
    parser.add_argument("--tags", action="store_true", help="Read ID3/metadata tags (slower)")
    parser.add_argument("--deep", action="store_true", help="Include per-genre/artist/year breakdowns")
    args = parser.parse_args()

    root = args.path
    if not os.path.isdir(root):
        print(f"Error: not a directory: {root}")
        sys.exit(1)

    print(f"Scanning: {root}")
    audio_files, other_files, other_sizes, total_dirs, errors = scan_files(root)
    print(f"Found {len(audio_files):,} audio files in {total_dirs:,} directories")

    tag_data = None
    if args.tags:
        print("Reading tags...")
        tag_data = read_tags(audio_files)

    print_report(audio_files, other_files, other_sizes, total_dirs, errors,
                 tag_data=tag_data, deep=args.deep)


if __name__ == "__main__":
    main()
