# SRXMDL – SiriusXM Monitor & Downloader

![SRXDL App](https://raw.githubusercontent.com/ZynthZWorkZ/SRXMDL/refs/heads/main/Githubimages/main.png)

 Application to Download Podcast & Music Content From Sirius XM 📻


## Prerequisites
- Active SiriusXM subscription 📻
- Google Chrome 🌐
- .NET 9 SDK (for build/run) 🟪
- ffmpeg ( for playback, downloads & conversions) ♻
- yt-dlp (For Podcast downloads & Video Downloads) ⬇
- pycryptodomex (optional but yt-dlp will download AES protected m3u8's faster if installed) ⚠


## Installation

### 1. Install .NET 9.0 SDK
Download and install the .NET 9.0 SDK for Windows:
- [Download .NET 9.0 SDK for Windows](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)

Verify installation:
```bash
dotnet --version
```

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
1. Launch the app and click **Start Monitoring** - a Chrome window will open.
2. Enter your SiriusXM credentials in the login window. Your password will be saved as a hash for future use and security.
3. After entering your credentials, start monitoring. The app will create a cookie file for automatic login on future sessions.
4. Once auto-logged in, simply use the app normally. Capture begins automatically - anything you play will be monitored and sent to the Stream Activity.
5. Captured streams will have the following action buttons:
   - **Copy URL**: Copy the stream URL to your clipboard
   - **Download**: Download the captured stream to your local storage
   - **Play**: Play the stream directly in the app (Note: Some streams won't have this button due to AES encryption)
   - **Copy Metadata**: Copy metadata from now playing stream information for streams with uncaptured metadata
6. In the download window, you can:
   - Select different audio quality options for audio streams
   - For video streams, select **MP4** format from the dropdown menu

Currently Working & Improving on :

🛠Better Metadata Captures 
🛠Full Channel Track list coming soon  
🛠Mac OS Version 
🛠CLI commands
