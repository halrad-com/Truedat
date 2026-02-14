#!/usr/bin/env python3
"""
Build script for Truedat - creates a single-file executable using PyInstaller.

Usage:
    python build.py

Output:
    dist/truedat.exe (Windows) or dist/truedat (Linux/Mac)

Prerequisites:
    pip install pyinstaller essentia numpy
"""

import subprocess
import sys
import os

def main():
    # Get the directory containing this script
    script_dir = os.path.dirname(os.path.abspath(__file__))
    src_dir = os.path.join(script_dir, "src")
    analyze_py = os.path.join(src_dir, "analyze.py")

    if not os.path.exists(analyze_py):
        print(f"Error: {analyze_py} not found")
        sys.exit(1)

    # PyInstaller command for single-file executable
    cmd = [
        sys.executable, "-m", "PyInstaller",
        "--onefile",                    # Single executable
        "--name", "truedat-analyze",    # Output name (called by truedat.exe)
        "--distpath", os.path.join(script_dir, "dist"),
        "--workpath", os.path.join(script_dir, "build"),
        "--specpath", script_dir,
        # Hidden imports that PyInstaller might miss
        "--hidden-import", "essentia",
        "--hidden-import", "essentia.standard",
        "--hidden-import", "numpy",
        # Collect all essentia data files (models, etc.)
        "--collect-all", "essentia",
        analyze_py
    ]

    print("Building Truedat with PyInstaller...")
    print(f"Command: {' '.join(cmd)}")
    print()

    result = subprocess.run(cmd, cwd=script_dir)

    if result.returncode == 0:
        if sys.platform == "win32":
            exe_path = os.path.join(script_dir, "dist", "truedat.exe")
        else:
            exe_path = os.path.join(script_dir, "dist", "truedat")

        if os.path.exists(exe_path):
            size_mb = os.path.getsize(exe_path) / (1024 * 1024)
            print(f"\nSuccess! Executable created: {exe_path}")
            print(f"Size: {size_mb:.1f} MB")
        else:
            print("\nBuild completed but executable not found at expected location")
    else:
        print(f"\nBuild failed with exit code {result.returncode}")
        sys.exit(result.returncode)

if __name__ == "__main__":
    main()
