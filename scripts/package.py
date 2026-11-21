#!/usr/bin/env python3
"""Package the compiled plugin without bundling Jellyfin's own assemblies."""
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parents[1]
project = ET.parse(root / "src/Jellyfin.Plugin.SubtitleDownload.csproj").getroot()
version = project.findtext(".//Version")
assert version is not None
repository = "sjtrny/jellyfin-plugin-subtitle-download"
output = root / "artifacts"
output.mkdir(exist_ok=True)
dll = root / "src/bin/Release/net10.0/publish/Jellyfin.Plugin.SubtitleDownload.dll"
metadata = {
    "guid": "b8ec4802-7283-4cb3-b4b3-1f7d68b293ca",
    "name": "Subtitle Download",
    "description": "Download every detected subtitle from the movie or episode menu.",
    "overview": "Download all subtitles as a ZIP file.",
    "owner": "sjtrny",
    "category": "General",
    "version": version,
    "targetAbi": "12.3.0.0",
    "status": "Active",
    "autoUpdate": True,
}
archive = output / f"subtitle-download-{version}-jellyfin12.3.zip"
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
    for name, content in {
        dll.name: dll.read_bytes(),
        "meta.json": (json.dumps(metadata, indent=2) + "\n").encode(),
        "README.md": (root / "README.md").read_bytes(),
        "LICENSE": (root / "LICENSE").read_bytes(),
    }.items():
        entry = zipfile.ZipInfo(name)
        entry.external_attr = 0o100644 << 16
        entry.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(entry, content)

# Jellyfin uses MD5 for its repository checksum; the release also supplies SHA-256.
manifest_path = root / "manifest.json"
manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else []
package = next((item for item in manifest if item["guid"] == metadata["guid"]), None)
if package is None:
    package = {key: metadata[key] for key in ["guid", "name", "description", "overview", "owner", "category"]}
    package["versions"] = []
    manifest.append(package)
entry = next((item for item in package["versions"] if item["version"] == version), None)
if entry is None:
    entry = {"version": version, "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")}
    package["versions"].append(entry)
entry.update({
    "changelog": "Adds Download subtitles below Download. Exports external and embedded subtitle tracks as a ZIP, with download permissions, cancellation and partial-failure reporting.",
    "targetAbi": metadata["targetAbi"],
    "sourceUrl": f"https://github.com/{repository}/releases/download/v{version}/{archive.name}",
    "checksum": hashlib.md5(archive.read_bytes(), usedforsecurity=False).hexdigest(),
})
package["versions"].sort(key=lambda item: tuple(int(part) for part in item["version"].split(".")), reverse=True)
manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")

source_archive = output / f"subtitle-download-{version}-source.zip"
source_files = [root / name for name in [".gitignore", "global.json", "Directory.Build.props", "README.md", "LICENSE", "manifest.json"]]
for directory in ["src", "scripts", "tests", "docs"]:
    source_files.extend(path for path in (root / directory).rglob("*")
                        if path.is_file() and not {"bin", "obj", "__pycache__"}.intersection(path.relative_to(root).parts))
with zipfile.ZipFile(source_archive, "w", zipfile.ZIP_DEFLATED) as z:
    for path in sorted(source_files):
        entry = zipfile.ZipInfo("jellyfin-plugin-subtitle-download/" + path.relative_to(root).as_posix())
        entry.external_attr = 0o100644 << 16
        entry.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(entry, path.read_bytes())

for artifact in [archive, source_archive]:
    checksum = hashlib.sha256(artifact.read_bytes()).hexdigest()
    artifact.with_suffix(".zip.sha256").write_text(f"{checksum}  {artifact.name}\n")
    print(artifact)
    print(f"SHA-256: {checksum}")
