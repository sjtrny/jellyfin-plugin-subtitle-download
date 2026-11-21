#!/usr/bin/env python3
"""Create small fixture videos. Optional bitmap samples come from FFmpeg's corpus."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("directory", type=Path)
args = parser.parse_args()
root = args.directory.resolve()
root.mkdir(parents=True, exist_ok=True)
scratch = root / "samples"
scratch.mkdir(exist_ok=True)


def run(*arguments):
    subprocess.run(["ffmpeg", "-v", "error", "-y", *map(str, arguments)], check=True)


def sample(name, url):
    path = scratch / name
    if not path.exists():
        path.write_bytes(urllib.request.urlopen(url, timeout=30).read())
    return path


def first_timestamp(path):
    data = json.loads(subprocess.check_output([
        "ffprobe", "-v", "error", "-select_streams", "s:0", "-show_packets", "-of", "json", str(path)
    ]))
    return float(data["packets"][0]["pts_time"])


english = scratch / "english.srt"
english.write_text("1\n00:00:00,500 --> 00:00:02,000\nFirst English subtitle.\n\n2\n00:00:03,000 --> 00:00:04,500\nSecond English subtitle.\n", encoding="utf-8")
spanish = scratch / "spanish.srt"
spanish.write_text("1\n00:00:00,500 --> 00:00:02,000\nSubtítulo español.\n", encoding="utf-8")
ass = scratch / "french.ass"
run("-i", spanish, ass)
pgs = sample("pgs.mkv", "https://samples.ffmpeg.org/sub/PGS/supsample.mkv")
dvd = sample("dvd.vob", "https://samples.ffmpeg.org/sub/DVD-short/dvd-subtitle-problem.vob")
idx = sample("vobsub.idx", "https://samples.ffmpeg.org/sub/VOB/X.The.Movie.DVDivX-SChiZO.idx")
sub = sample("vobsub.sub", "https://samples.ffmpeg.org/sub/VOB/X.The.Movie.DVDivX-SChiZO.sub")

movie = root / "media/Movies/Subtitle Test (2026)"
movie.mkdir(parents=True, exist_ok=True)
video = movie / "Subtitle Test (2026).mkv"
run("-f", "lavfi", "-i", "color=c=0x183848:s=320x180:d=5:r=24",
    "-i", english, "-i", ass, "-itsoffset", str(-first_timestamp(pgs)), "-i", pgs,
    "-itsoffset", str(-first_timestamp(dvd)), "-i", dvd,
    "-map", "0:v", "-map", "1:s", "-map", "2:s", "-map", "3:s", "-map", "4:s",
    "-c:v", "libx264", "-preset", "ultrafast", "-c:s", "copy", "-t", "5",
    "-metadata:s:s:0", "language=eng", "-disposition:s:0", "forced",
    "-metadata:s:s:1", "language=fra", "-metadata:s:s:1", "title=French SDH", "-disposition:s:1", "hearing_impaired",
    "-metadata:s:s:2", "language=jpn", "-metadata:s:s:3", "language=deu", video)
for source, suffix in [(english, ".en.srt"), (spanish, ".es.srt"), (idx, ".it.idx"), (sub, ".it.sub")]:
    shutil.copyfile(source, movie / (video.stem + suffix))
run("-i", spanish, movie / (video.stem + ".pt.vtt"))
(movie / "movie.nfo").write_text("<movie><title>Subtitle Test</title><year>2026</year><plot>A five-second test video with text and bitmap subtitle tracks.</plot></movie>")

for name, folder in [("No Subtitles", "Movies"), ("Other Movie", "Movies"), ("Hidden Movie", "Hidden")]:
    target = root / f"media/{folder}/{name} (2026)"
    target.mkdir(parents=True, exist_ok=True)
    run("-i", video, "-map", "0:v", "-c", "copy", target / f"{name} (2026).mkv")
    if name != "No Subtitles":
        shutil.copyfile(spanish, target / f"{name} (2026).es.srt")
    (target / "movie.nfo").write_text(f"<movie><title>{name}</title><year>2026</year></movie>")

show = root / "media/Shows/Subtitle Series/Season 01"
show.mkdir(parents=True, exist_ok=True)
shutil.copyfile(video, show / "Subtitle Series - S01E01 - Test Episode.mkv")
(show.parent / "tvshow.nfo").write_text("<tvshow><title>Subtitle Series</title></tvshow>")
(show / "Subtitle Series - S01E01 - Test Episode.nfo").write_text("<episodedetails><title>Test Episode</title><season>1</season><episode>1</episode></episodedetails>")
print(root / "media")
