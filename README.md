# SRXDL - SiriusXM Downloader

![SRXDL App](https://raw.githubusercontent.com/ZynthZWorkZ/SRXMDL/refs/heads/main/Githubimages/main.png)

A powerful desktop application for downloading and managing SiriusXM content.

## Prerequisites

- Active SiriusXM subscription
- Google Chrome web browser
- FFMPEG (for downloading and playing streams)

## Installation

### Option 1: Download Latest Release
Download the latest release from the releases page.

### Option 2: Build from Source
```bash
dotnet build
```

## Setup

1. Export your SiriusXM cookies to a JSON file or Netscape format
2. Rename the file to either:
   - `temp.json` (for JSON format)
   - `temp.txt` (for Netscape format)
3. Place the file in the cookies directory

The application will automatically load your cookies and sign you into your SiriusXM account.

## Features

- **Stream Detection**
  - Supports MP4 and MP3 streams
  - M3U8 stream support (coming soon)

- **Stream Management**
  - View stream activity with track names
  - Fallback to "Unnamed MP4/MP3" when metadata is unavailable
  - Multiple quality options for downloads

- **Stream Actions**
  - Copy stream URL
  - Play stream directly
  - Download stream in various quality options

- **Artist Stations**
  - Automatic addition to favorites when visiting artist station pages
  - Direct playback from the application (FFPLAY)

- **Now Playing Features**
  - Lyrics display for currently playing songs
  - Enhanced lyrics functionality (in development)

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.





