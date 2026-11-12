# Jellyfin Download Transcoder

A Jellyfin server plugin that makes **downloads smaller**. When an app downloads a video for offline use, for example with the download button in the official Jellyfin Android app, the plugin sends a transcoded MP4 at a resolution and bitrate you choose instead of the original file.

No app changes are needed. The apps call the server's `GET /Items/{id}/Download` endpoint, and the plugin swaps the response after Jellyfin has done its usual checks (login, the user's download permission, and the activity log).

Requires **Jellyfin 12.x**.

## What it does

- Transcodes with Jellyfin's own ffmpeg, the one set under Dashboard → Playback → Transcoding.
- Uses defaults suited to phones: 720p H.264 at 2 Mbit/s with stereo AAC, which is about 0.9 GB per hour. Text subtitles are embedded, and HDR is tone mapped to SDR.
- Sends the original file untouched when it is already within your limits.
- Caches finished transcodes, so a second download is instant and supports resuming and seeking. The oldest ones are removed when the cache reaches its size limit.
- Can pre-transcode in advance. The **Pre-transcode downloads** scheduled task runs daily at 3 AM and prepares the newest items in the libraries you pick.
- Handles items with no ready copy in one of three ways:
  - **Live** (the default): transcodes while the file downloads. The app can't show a percentage because the size isn't known in advance. The result is also kept in the cache.
  - **Queue**: answers "try again later" (503) and prepares the copy in the background.
  - **Original**: sends the original file, so only pre-transcoded copies are ever used.
- Lets you choose which apps get the smaller files (Dashboard → Devices shows app names such as "Jellyfin Android"). Adding `?original=true` to a download URL always returns the original.
- Supports hardware encoding through the encoder name (`h264_nvenc`, `h264_qsv`, `h264_vaapi`, …), extra input arguments and a custom filter, all set on the settings page.

## Install

1. Download `download-transcoder.zip` from the latest release, or build it yourself (see below).
2. Create a folder named `DownloadTranscoder_1.0.0.0` inside Jellyfin's `plugins` folder:
   - Docker: `/config/plugins`
   - Debian/Ubuntu: `/var/lib/jellyfin/plugins`
   - Windows: `%ProgramData%\Jellyfin\Server\plugins`
3. Put `Jellyfin.Plugin.DownloadTranscoder.dll` inside that folder.
4. Restart Jellyfin.
5. Open Dashboard → Plugins → Download Transcoder to change the settings.

## Build

```sh
dotnet test Jellyfin.Plugin.DownloadTranscoder.slnx   # needs ffmpeg and ffprobe on PATH
dotnet publish Jellyfin.Plugin.DownloadTranscoder -c Release -o out
```

Pushing a `v*` tag makes GitHub Actions attach the zip to a release.

## Limitations

- Live downloads are fragmented MP4. Some players can't seek inside them, but a cached or pre-transcoded copy always can. The MPEG-TS live option seeks better in Android players, but it drops subtitles.
- Image subtitles (PGS or VobSub) are dropped, because MP4 can't carry them.
- Only video is affected. Music downloads are unchanged.
