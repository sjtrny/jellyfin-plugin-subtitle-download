# Verification

Run these checks only against a disposable Jellyfin 12.3 server. They create
users and libraries, change test-user permissions, scan test media and briefly
hide one fixture subtitle. The integration script requires the expected
server ID and refuses to modify a different server.

Requirements: Docker, Python 3 with `requests`, FFmpeg/ffprobe, Node.js and
Playwright with Chromium. Build the plugin first, as described in the main
README.

1. Run `python3 tests/prepare_media.py /tmp/subtitle-download-review`.
   This creates small test videos under `/tmp/subtitle-download-review/media`.
   Text subtitles are synthetic; bitmap fixtures are downloaded from the
   [FFmpeg sample corpus](https://samples.ffmpeg.org/sub/). The samples are
   not included in the plugin or source release.
2. Start an isolated `jellyfin/jellyfin:12.3` container named
   `subtitle-download-review`, with separate `/config` and `/cache` volumes.
   Mount only the generated `media` directory at `/media`, writable so the
   tests can exercise missing-file handling. Publish its port on localhost,
   for example `127.0.0.1:18096:8096`.
3. Extract the plugin release into the container's
   `/config/plugins/Subtitle Download_1.0.0.0/` and restart that container.
4. Read the disposable server's `Id` from
   `http://127.0.0.1:18096/System/Info/Public`. Run:

   ```sh
   python3 tests/integration.py \
     --url http://127.0.0.1:18096 \
     --server-id EXPECTED_DISPOSABLE_SERVER_ID \
     --state /tmp/subtitle-download-review/results/review-state.json \
     --media /tmp/subtitle-download-review/media

   node tests/browser.cjs /tmp/subtitle-download-review/results/review-state.json
   ```

   Set `PLAYWRIGHT_MODULE` to an existing Playwright module path if it is not
   installed locally. The state file holds a generated test password and is
   created with mode `0600`; keep it out of source control and release assets.

The API checks cover plugin loading, automatic HTML integration, exact
external-file contents, VobSub pairs, embedded SRT/ASS/PGS/DVD extraction,
language/forced/SDH filenames, movie and episode exports, missing files,
empty results, authentication, download permissions, library restrictions
and temporary-file cleanup.

The browser checks cover menu position and item selection, an actual ZIP
download, the original movie download, repeated and right-click menus,
episodes, cancellation, mobile layout and users without download permission.
They save screenshots and JSON results next to the state file.

The release was also checked with File Transformation 3.0.3 installed and
with Jellyfin configured under the `/jf-review` base URL.
