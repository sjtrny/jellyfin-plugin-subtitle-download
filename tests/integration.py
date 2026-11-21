#!/usr/bin/env python3
"""Exercise the plugin in a disposable Jellyfin server, never the production server."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import secrets
import subprocess
import time
import zipfile
import requests

parser = argparse.ArgumentParser()
parser.add_argument("--url", required=True)
parser.add_argument("--server-id", required=True, help="Expected disposable Jellyfin server ID")
parser.add_argument("--state", required=True, type=Path)
parser.add_argument("--media", required=True, type=Path)
parser.add_argument("--container", default="subtitle-download-review")
args = parser.parse_args()
base = args.url.rstrip("/")
session = requests.Session()
server = session.get(base + "/System/Info/Public", timeout=10).json()
assert server["Id"] == args.server_id, "Refusing to modify an unexpected server"
state = json.loads(args.state.read_text()) if args.state.exists() else {"password": secrets.token_urlsafe(24)}
args.state.parent.mkdir(parents=True, exist_ok=True)
args.state.write_text(json.dumps(state))
args.state.chmod(0o600)


def call(method, path, **kwargs):
    response = session.request(method, base + path, timeout=120, **kwargs)
    response.raise_for_status()
    return response.json() if response.content and "json" in response.headers.get("Content-Type", "") else response


if not server["StartupWizardCompleted"]:
    call("GET", "/Startup/User")
    call("POST", "/Startup/Configuration", json={"ServerName": "Subtitle Download Review", "UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
    call("POST", "/Startup/User", json={"Name": "review", "Password": state["password"]})
    # First-time authentication establishes the admin session before completing setup.

auth_header = 'MediaBrowser Client="SubtitleDownloadTests", Device="Test", DeviceId="subtitle-download-tests", Version="1.0"'
auth = call("POST", "/Users/AuthenticateByName", json={"Username": "review", "Pw": state["password"]}, headers={"Authorization": auth_header})
admin_id = auth["User"]["Id"]
session.headers["Authorization"] = auth_header + ', Token="' + auth["AccessToken"] + '"'
if not server["StartupWizardCompleted"]:
    call("POST", "/Startup/Complete")
state.update({"url": base, "serverId": server["Id"], "adminId": admin_id})

# Older system FFmpeg builds omit Matroska's SDH flag. Use the server's encoder
# to make that fixture explicit, so the filename assertion exercises real metadata.
test_video = "/media/Movies/Subtitle Test (2026)/Subtitle Test (2026).mkv"
subprocess.run(["docker", "exec", args.container, "/usr/lib/jellyfin-ffmpeg/ffmpeg", "-v", "error", "-y", "-i", test_video,
                "-map", "0", "-c", "copy", "-disposition:s:1", "hearing_impaired", "/tmp/subtitle-review-sdh.mkv"], check=True)
subprocess.run(["docker", "exec", args.container, "mv", "/tmp/subtitle-review-sdh.mkv", test_video], check=True)

libraries = call("GET", "/Library/VirtualFolders")
for name, kind in [("Movies", "movies"), ("Hidden", "movies"), ("Shows", "tvshows")]:
    if not any(v["Name"] == name for v in libraries):
        call("POST", "/Library/VirtualFolders", params={"name": name, "collectionType": kind, "refreshLibrary": "false"}, json={"LibraryOptions": {
            "PathInfos": [{"Path": "/media/" + name}], "EnableRealtimeMonitor": False,
            "EnableInternetProviders": False, "EnableEmbeddedTitles": False,
            "EnableChapterImageExtraction": False, "EnableTrickplayImageExtraction": False,
            "TypeOptions": [{"Type": t, "MetadataFetchers": [], "ImageFetchers": []} for t in ["Movie", "Series", "Season", "Episode"]]
        }})
call("POST", "/Library/Refresh")
deadline = time.monotonic() + 90
while True:
    items = call("GET", f"/Users/{admin_id}/Items", params={"Recursive": "true", "IncludeItemTypes": "Movie,Episode", "Fields": "MediaSources,MediaStreams,Path,CanDownload"})["Items"]
    if len(items) >= 5 and any(x["Name"] == "Subtitle Test" and any(s.get("IsHearingImpaired") for s in x.get("MediaStreams", [])) for x in items):
        break
    assert time.monotonic() < deadline, [(x["Name"], len(x.get("MediaStreams", []))) for x in items]
    time.sleep(1)
by_name = {x["Name"]: x for x in items}
movie = by_name["Subtitle Test"]
state["items"] = {x["Name"]: x["Id"] for x in items}
args.state.write_text(json.dumps(state))
args.state.chmod(0o600)
checks = []


def passed(name, **extra):
    checks.append({"check": name, "passed": True, **extra})
    print("PASS", name, flush=True)


plugins = call("GET", "/Plugins")
assert any(p["Name"] == "Subtitle Download" and p["Status"] == "Active" for p in plugins), plugins
passed("plugin loads as Active")
for path in ["/web/", "/web/index.html"]:
    response = requests.get(base + path, headers={"Accept-Encoding": "gzip", "If-None-Match": '"stale"', "Range": "bytes=0-50"}, timeout=20)
    assert response.status_code == 200
    assert response.text.count('id="subtitle-download-plugin"') == 1
    assert int(response.headers["Content-Length"]) == len(response.content)
passed("automatic web integration, range and cache handling")

download_path = f"/SubtitleDownload/Items/{movie['Id']}/Download"
response = session.get(base + download_path, timeout=120)
assert response.status_code == 200, response.text
archive = zipfile.ZipFile(io.BytesIO(response.content))
names = archive.namelist()
assert archive.testzip() is None
assert response.headers["X-Subtitle-Failed"] == "0", names
assert int(response.headers["X-Subtitle-Count"]) == sum(s["Type"] == "Subtitle" for s in movie["MediaStreams"]), names
assert len(names) == 9, names
for suffix in [".en.srt", ".es.srt", ".it.idx", ".it.sub", ".pt.vtt"]:
    name = next(n for n in names if n.endswith(suffix))
    original = args.media / "Movies/Subtitle Test (2026)" / name
    assert archive.read(name) == original.read_bytes(), name
passed("all external files are byte-for-byte identical, including VobSub pair")
for extension, codec in [("srt", "subrip"), ("ass", "ass"), ("sup", "hdmv_pgs_subtitle"), ("mks", "dvd_subtitle")]:
    name = next(n for n in names if ".track-" in n and n.endswith("." + extension))
    target = args.state.parent / ("exported." + extension)
    target.write_bytes(archive.read(name))
    probe = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-show_streams", "-show_packets", "-of", "json", str(target)]))
    assert probe["streams"][0]["codec_name"] == codec, probe
    assert len(probe.get("packets", [])) > 0, probe
assert any(".eng.forced.track-" in n for n in names), names
assert any(".fra.sdh.track-" in n for n in names), names
passed("embedded SRT, ASS, PGS and DVD subtitles remain readable with language/flags")
(args.state.parent / "sample-subtitles.zip").write_bytes(response.content)

no_id = by_name["No Subtitles"]["Id"]
assert call("GET", f"/SubtitleDownload/Items/{no_id}/Info")["Count"] == 0
assert session.get(base + f"/SubtitleDownload/Items/{no_id}/Download", timeout=20).status_code == 404
passed("no-subtitle videos return an explicit empty result")
assert requests.get(base + download_path, timeout=20).status_code == 401
passed("anonymous downloads are rejected")

libraries = call("GET", "/Library/VirtualFolders")
movies_library = next(x["ItemId"] for x in libraries if x["Name"] == "Movies")
for username, can_download in [("allowed", True), ("blocked", False)]:
    all_users = call("GET", "/Users")
    user = next((u for u in all_users if u["Name"] == username), None)
    if user is None:
        user = call("POST", "/Users/New", json={"Name": username, "Password": state["password"]})
    policy = call("GET", "/Users/" + user["Id"])["Policy"]
    policy.update({"IsAdministrator": False, "EnableContentDownloading": can_download, "EnableSubtitleManagement": False,
                   "EnableAllFolders": False, "EnabledFolders": [movies_library]})
    call("POST", f"/Users/{user['Id']}/Policy", json=policy)
    login = call("POST", "/Users/AuthenticateByName", json={"Username": username, "Pw": state["password"]}, headers={"Authorization": auth_header})
    headers = {"Authorization": 'MediaBrowser Token="' + login["AccessToken"] + '"'}
    result = requests.get(base + download_path, headers=headers, timeout=120)
    assert result.status_code == (200 if can_download else 403), (username, result.status_code, result.text[:200])
    hidden = requests.get(base + f"/SubtitleDownload/Items/{by_name['Hidden Movie']['Id']}/Download", headers=headers, timeout=20)
    assert hidden.status_code in (403, 404), hidden.status_code
passed("download permission and library restrictions enforced; subtitle-management permission unnecessary")

episode = next(x for x in items if x["Type"] == "Episode")
episode_response = session.get(base + f"/SubtitleDownload/Items/{episode['Id']}/Download", timeout=120)
assert episode_response.status_code == 200
assert episode_response.headers["X-Subtitle-Count"] == "4"
passed("episode downloads include all four embedded tracks")

# Simulate a stale library record without asking Jellyfin to refresh it.
missing = "/media/Movies/Subtitle Test (2026)/Subtitle Test (2026).es.srt"
subprocess.run(["docker", "exec", args.container, "mv", missing, missing + ".hold"], check=True)
try:
    partial = session.get(base + download_path, timeout=120)
    assert partial.status_code == 200
    assert partial.headers["X-Subtitle-Failed"] == "1"
    z = zipfile.ZipFile(io.BytesIO(partial.content))
    assert "SUBTITLE-EXPORT-ERRORS.txt" in z.namelist()
    assert len(z.namelist()) == 9
finally:
    subprocess.run(["docker", "exec", args.container, "mv", missing + ".hold", missing], check=True)
passed("missing tracks produce an explicit partial-download report")

for _ in range(20):
    leftovers = subprocess.check_output(["docker", "exec", args.container, "find", "/cache/subtitle-download", "-mindepth", "1", "-print"]).decode()
    if not leftovers.strip(): break
    time.sleep(0.1)
assert not leftovers.strip(), leftovers
passed("temporary exports are removed")
args.state.write_text(json.dumps(state))
args.state.chmod(0o600)
(args.state.parent / "integration-results.json").write_text(json.dumps(checks, indent=2) + "\n")
print(f"{len(checks)} integration checks passed.")
