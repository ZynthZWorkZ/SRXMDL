# SRXMDL – SiriusXM Monitor & Downloader

![SRXDL App](https://raw.githubusercontent.com/ZynthZWorkZ/SRXMDL/refs/heads/main/Githubimages/main.png)

Modern WPF app to monitor SiriusXM traffic, capture stream URLs, and download MP3/MP4/M3U8 with embedded metadata and cover art.

## Prerequisites
- Active SiriusXM subscription
- Google Chrome
- .NET 9 SDK (for build/run)
- ffmpeg (playback, downloads,conversions)
- yt-dlp (M3U8 downloads)
- pycryptodomex (optional but yt-dlp will download AES protected m3u8's faster if installed)

## Quick Start
```bash
dotnet run
```

Or

## Build
```bash
dotnet build
```

## Setup
1) Launch the app click Start Monetring and a Chrome window will open.
2) Manually sign in to your SiriusXM account in that window.
3) Once signed in, monitoring and capture begin automatically anything you play will be monitored and sent to the Stream Activity.

## What it does
- **Network capture**: MP3, MP4, M3U8 streams .
- **Stream activity**: Live table with track/artist/art, dedupe handling, copy/play/download actions.
- **Downloads**:
  - MP3: embedded title/artist + cover art (downloaded and muxed).
  - WAV: title/artist, cover URL stored in comment.
  - Optional filename; if blank, auto-uses “Artist - Track”.
  - Status file `download_status.txt` indicates success/error (auto-cleaned after a delay).
- **Artist stations**: Favorites, playback, info fetch.
- **Lyrics**: On-demand lyrics window for now playing.(needs work )
- **Responsive UI**: Dark theme, scaled layout, collapsible artist panel on narrow widths.



Full Channel Track list coming soon & Auto Sign in ⚠

## Contributing
PRs welcome. Please include a brief description and any relevant reproduction steps for stream cases.***