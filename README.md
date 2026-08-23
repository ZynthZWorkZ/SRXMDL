# SRXMDL – SiriusXM Monitor & Downloader

![SRXDL App](https://raw.githubusercontent.com/ZynthZWorkZ/SRXMDL/refs/heads/main/Githubimages/main.png)

 Application to Download Podcast & Music Content From Sirius XM 📻


## Prerequisites
- Active SiriusXM subscription 📻
- .NET 10 SDK (for build/run) 🟪
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (usually preinstalled on Windows 10/11) 🌐
- ffmpeg (for playback, downloads & conversions) ♻
- yt-dlp (For Podcast downloads & Video Downloads) ⬇
- pycryptodomex (optional but yt-dlp will download AES protected m3u8's faster if installed) ⚠


## Installation

### 1. Install .NET 10 SDK
Download and install the .NET SDK for Windows:
- [Download .NET SDK for Windows](https://dotnet.microsoft.com/download)

Verify installation:
```bash
dotnet --version
```

### 1b. Install WebView2 Runtime (if needed)
Most Windows 10/11 systems already have this. If the embedded player fails to load:
- [Download WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)

### 2. Install yt-dlp
Download and install yt-dlp for Windows:
- **Option 1**: Download the `.exe` file from [yt-dlp Releases](https://github.com/yt-dlp/yt-dlp) and place it in your PATH
- **Option 2**: Install via pip:
  ```bash
  pip install yt-dlp
  ```

### 3. Install pycryptodomex (Optional but Recommended)
For faster AES-protected m3u8 downloads:
```bash
pip install pycryptodomex
```

### 4. Install ffmpeg
Install ffmpeg for Windows:
- **Option 1**: Download from [ffmpeg.org](https://ffmpeg.org/download.html)
- **Option 2**: Use Windows Package Manager:
  ```bash
  winget install ffmpeg
  ```

## Quick Start
After installing all prerequisites:
```bash
dotnet run
```

## Build
```bash
dotnet build
``` 
## Or Just Download Latest after installing ffmpeg and yt-dlp ! 🔥




## Usage
1. Launch the app — one wide window opens with the **SiriusXM Player** embedded on the left half and your capture panel on the right half. Sign in directly in the player, or save credentials via the login button for optional auto sign-in.
2. Click **Start Monitoring** to begin recording captured streams to the list (network capture itself is always attached in the background, so nothing is missed while you're signing in or already playing).
3. Switch between the **Stream Activity** and **Artists** tabs on the right half. Streams support copy, download, play, and metadata actions.
4. Visit artist pages in the player to populate the **Artists** tab.
5. Use the download window for quality/format options; ffmpeg and yt-dlp handle conversions.



Currently Working & Improving on :

🛠Better Metadata Captures 
🛠Full Channel Track list coming soon  
🛠Mac OS Version 
🛠CLI commands
