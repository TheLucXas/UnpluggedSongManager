"""Build an original silent USM for testing A+X intro skip.

Requires FFmpeg and Pillow. No downloaded chart/audio is used.
Usage: python tools/GenerateIntroSkipFixture.py --output <path.usm>
"""
import argparse
import io
import json
import zipfile
import subprocess
import shutil
from pathlib import Path

from PIL import Image, ImageDraw

parser = argparse.ArgumentParser()
parser.add_argument("--output", type=Path, required=True)
parser.add_argument("--ffmpeg", default=shutil.which("ffmpeg") or str(Path(__file__).resolve().parent.parent / "ffmpeg.exe"))
args = parser.parse_args()
output = args.output
output.parent.mkdir(parents=True, exist_ok=True)

def silent_ogg(seconds):
    return subprocess.run([args.ffmpeg, "-v", "error", "-f", "lavfi", "-i",
                           "anullsrc=r=22050:cl=mono", "-t", str(seconds),
                           "-c:a", "libvorbis", "-f", "ogg", "pipe:1"],
                          check=True, capture_output=True).stdout

cover = Image.new("RGB", (512, 512), "#031524")
draw = ImageDraw.Draw(cover)
draw.text((40, 120), "USM INTRO SKIP TEST", fill="#85c1f5", font_size=30)
draw.text((40, 220), "First note: 30 seconds", fill="#e6f2fd", font_size=28)
draw.text((40, 280), "A + X -> 27.5 seconds", fill="#ef37d8", font_size=26)
cover_bytes = io.BytesIO()
cover.save(cover_bytes, format="JPEG", quality=85)
meta = {
    "BandName": "USM Test", "SongName": "Intro Skip - 30s Silence",
    "FileName": output.stem, "SongID": "CUSTOM_" + output.stem,
    "InitialDelay": 0, "IsBuiltIn": False, "IsMultiTrack": False,
    "AudioLayout": "single-track", "AudioStemCount": 1,
    "SourceDifficultyCount": 4, "SourceChartNoteCount": 8,
    "ChartCoverageRatio": 1, "SongDurationSeconds": 45,
    "CompatibilityProfileVersion": 2, "IsIdealForUnplugged": False,
    "LevelAuthorName": "USM diagnostic fixture", "ChartParserVersion": 7,
    "DiagnosticFixture": "intro-skip-first-note-30s"
}
with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as archive:
    archive.writestr("meta.json", json.dumps(meta))
    archive.writestr("cover.jpg", cover_bytes.getvalue())
    archive.writestr("song.ogg", silent_ogg(45))
    archive.writestr("sample.ogg", silent_ogg(5))
    for difficulty in ("SuperEasy", "Easy", "Normal", "Hard"):
        chart = {"SchemaVersion": 3, "Difficulty": "songdata-" + difficulty + ".json",
                 "BPM": 120, "Duration": 45, "BPMGrid": [{"Time": 0, "BPM": 120}],
                 "HandNotes": [], "ControllerNotes": []}
        for i in range(8):
            common = {"Time": 30 + i, "Duration": 0, "StringLane": i % 4}
            chart["HandNotes"].append(dict(common, Fingers=["Index"], IsPullOff=False))
            chart["ControllerNotes"].append(dict(common, Buttons=[True, False], ChordRotation=0))
        archive.writestr("songdata-" + difficulty + ".json", json.dumps(chart))
print(output)
print("45s silent audio; first playable note at 30s in every mode/difficulty; skip target 27.5s.")
