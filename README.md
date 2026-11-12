# Jellyfin Download Transcoder

A Jellyfin server plugin that makes **downloads smaller**. When an app downloads a video for offline use, for example with the download button in the official Jellyfin Android app, the plugin sends a transcoded MP4 at a resolution and bitrate you choose instead of the original file.

No app changes are needed. The apps call the server's `GET /Items/{id}/Download` endpoint, and the plugin swaps the response after Jellyfin has done its usual checks (login, the user's download permission, and the activity log).

Requires **Jellyfin 12.x**.

## What it does

- Transcodes with Jellyfin's own ffmpeg, the one set under Dashboard → Playback → Transcoding.
- Uses **per-device profiles**, picked automatically on the server (see below).
- Embeds text subtitles and tone maps HDR to SDR.
- Sends the original file untouched when it is already within your limits.
- Caches finished transcodes, so a second download is instant and supports resuming and seeking. The oldest ones are removed when the cache reaches its size limit.
- Can pre-transcode in advance. The **Pre-transcode downloads** scheduled task runs daily at 3 AM and prepares the newest items in the libraries you pick.
- Handles items with no ready copy in one of three ways:
  - **Queue** (the default): answers "try again later" (503) and prepares the copy in the background. The official Android app retries by itself, after about 30 s, 1 min, 2 min, 4 min and so on, and the download completes once the copy is ready. If transcoding fails, the original is sent instead.
  - **Live**: transcodes while the file downloads. The size isn't known up front, so this is only used for apps that don't resume downloads; apps that send a `Range` header (such as the official Android app) still get Queue behaviour.
  - **Original**: sends the original file, so only pre-transcoded copies are ever used.
- Adding `?original=true` to a download URL always returns the original. `?profile=Tablet` forces a specific profile.
- Shows a **Download Activity** page in the dashboard sidebar (next to the **Download Transcoder** settings entry), with live progress (percent, speed, time left) for running transcodes, a history of past ones with their size savings and any errors, and the cached copies, each with a delete button. Running transcodes can be cancelled, and finished or failed ones retried.
- Has a **Test GPU** button that runs short 1080p H.264 and 4K 10-bit HEVC test transcodes through each path and reports what works and how fast.
- Encodes on the GPU set under Dashboard → Playback → Transcoding (NVENC, Quick Sync, VAAPI, VideoToolbox, AMF, RKMPP) when "Enable hardware encoding" is on there. If the GPU encode fails, it retries on the CPU. With Intel or AMD graphics using QSV or VAAPI on Linux, decoding, scaling and HDR tone mapping also run on the GPU (full pipeline). If that fails for a file, it falls back to GPU encode only, then to the CPU. You can also enter a specific encoder in a profile (for example `h264_nvenc`), and add extra input arguments or a custom filter.

## Device profiles

Every download uses one profile. These are the defaults, and you can change all of them in the plugin settings:

| Profile  | Used for            | Output                                   | Approx. size per hour |
|----------|---------------------|------------------------------------------|-----------------------|
| Phone    | phones              | 720p H.264, 2 Mbit/s, stereo AAC 128k    | 0.9 GB                |
| Tablet   | tablets             | 1080p H.264, 4 Mbit/s, stereo AAC 160k   | 1.9 GB                |
| TV       | TVs, sticks, boxes  | original file (or 1080p HEVC 8 Mbit/s 5.1 if you turn transcoding on) | |
| Computer | browsers, desktop apps | original file                         |                       |
| Other    | anything unrecognised | 1080p H.264, 4 Mbit/s, stereo          | 1.9 GB                |

The server picks the profile in this order:

1. A device you pinned to a profile on the settings page. The page lists every device that has signed in, what it was detected as, and which profile it currently gets.
2. A profile whose "match names" appear in the app name or device name, for example `Findroid` or `Pixel Tablet`.
3. The detected device kind. Detection uses the app name and device name that the app registered with the server (for example "Jellyfin Android" with "SM-X710", which is a Galaxy Tab), plus the browser's User-Agent.
4. The "Other" profile.

## Install

1. In Jellyfin, open Dashboard → Plugins → Repositories (Catalog → ⚙ on some versions) and add a repository:
   - Name: `Download Transcoder`
   - URL: `https://raw.githubusercontent.com/chendrak/jellyfin-plugin-download-transcoder/main/manifest.json`
2. Open the Catalog, install **Download Transcoder**, and restart Jellyfin.
3. Open **Download Transcoder** in the dashboard sidebar to review the profiles and devices.

New versions show up as updates in the plugin list.

To install by hand instead, download the zip from the latest release, put `Jellyfin.Plugin.DownloadTranscoder.dll` in a new folder such as `plugins/DownloadTranscoder_1.0.0.0` (`/config/plugins` on Docker, `/var/lib/jellyfin/plugins` on Debian/Ubuntu), and restart Jellyfin.

## Build

```sh
dotnet test Jellyfin.Plugin.DownloadTranscoder.slnx   # needs ffmpeg and ffprobe on PATH
dotnet publish Jellyfin.Plugin.DownloadTranscoder -c Release -o out
```

To release, bump `version` (and `changelog`) in `build.yaml` and push to `main`. The Release workflow tests, builds the zip, creates the GitHub release and adds the version to `manifest.json`. It can also be run by hand from the Actions tab.

## Limitations

- Live downloads are fragmented MP4. Some players can't seek inside them, but a cached or pre-transcoded copy always can. The MPEG-TS live option seeks better in Android players, but it drops subtitles.
- Image subtitles (PGS or VobSub) are dropped, because MP4 can't carry them.
- Only video is affected. Music downloads are unchanged.
