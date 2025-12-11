using System;
using System.Windows;
using System.Diagnostics;
using System.Windows.Controls;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text;

namespace SRXMDL.Download
{
    public partial class DownloadWindow : Window
    {
        private string _audioUrl;
        private readonly string _trackName;
        private readonly string _artistName;
        private readonly string _imageUrl;
        private enum AudioFormat { Mp3, Wav, Mp4 }

        public DownloadWindow(string audioUrl, string trackName, string artistName, string imageUrl)
        {
            InitializeComponent();
            _audioUrl = audioUrl;
            _trackName = string.IsNullOrWhiteSpace(trackName) ? "Unknown Track" : trackName;
            _artistName = string.IsNullOrWhiteSpace(artistName) ? "Unknown Artist" : artistName;
            _imageUrl = imageUrl ?? string.Empty;
            // Prefill with suggested name (optional)
            OutputFilenameTextBox.Text = BuildBaseName();
            FormatComboBox_SelectionChanged(null, null);
        }

        private string GetOutputFilename(string quality, AudioFormat format)
        {
            string baseName = GetSanitizedBaseName();
            string ext = format == AudioFormat.Mp3 ? "mp3" : "wav";
            // Do not append quality to title; use clean base name only
            return $"{baseName}.{ext}";
        }

        private string GetSanitizedBaseName()
        {
            var baseName = OutputFilenameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(baseName))
            {
                baseName = BuildBaseName();
            }

            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "output";
            }

            foreach (var c in Path.GetInvalidFileNameChars())
            {
                baseName = baseName.Replace(c, '_');
            }

            return baseName;
        }

        private string BuildBaseName()
        {
            if (!string.IsNullOrWhiteSpace(_artistName) && !string.IsNullOrWhiteSpace(_trackName))
            {
                return $"{_artistName} - {_trackName}";
            }
            if (!string.IsNullOrWhiteSpace(_trackName))
            {
                return _trackName;
            }
            return string.Empty;
        }

        private async void LowQualityDownload_Click(object sender, RoutedEventArgs e)
        {
            await RunDownloadPreset("low",
                wavCodec: "-acodec pcm_u8 -ar 22050 -ac 1",
                mp3Bitrate: "128k");
        }

        private async void StandardQualityDownload_Click(object sender, RoutedEventArgs e)
        {
            await RunDownloadPreset("standard",
                wavCodec: "-acodec pcm_s16le -ar 44100 -ac 2",
                mp3Bitrate: "192k");
        }

        private async void HighQualityDownload_Click(object sender, RoutedEventArgs e)
        {
            await RunDownloadPreset("high",
                wavCodec: "-acodec pcm_s24le -ar 48000 -ac 2",
                mp3Bitrate: "256k");
        }

        private async void HighestQualityDownload_Click(object sender, RoutedEventArgs e)
        {
            await RunDownloadPreset("max",
                wavCodec: "-acodec pcm_s32le -ar 96000 -ac 2",
                mp3Bitrate: "320k");
        }

        private async Task RunDownloadPreset(string qualityKey, string wavCodec, string mp3Bitrate)
        {
            var format = GetSelectedFormat();

            // If this is an M3U8 stream, use yt-dlp + captured key/bearer
            if (_audioUrl?.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var baseName = GetSanitizedBaseName();
                await RunM3u8WithYtDlpAsync(baseName, format);
                return;
            }

            var outputFile = GetOutputFilename(qualityKey, format);
            string tempCoverPath = null;

            var (command, coverPath) = await BuildFfmpegCommandAsync(format, wavCodec, mp3Bitrate, outputFile);
            tempCoverPath = coverPath;

            ExecuteProcessCommand(command, tempCoverPath);
        }

        private AudioFormat GetSelectedFormat()
        {
            var selected = FormatComboBox.SelectedItem as ComboBoxItem;
            var content = selected?.Content as string;
            if (string.Equals(content, "wav", StringComparison.OrdinalIgnoreCase))
                return AudioFormat.Wav;
            if (string.Equals(content, "mp4", StringComparison.OrdinalIgnoreCase))
                return AudioFormat.Mp4;
            return AudioFormat.Mp3;
        }

        private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var format = GetSelectedFormat();
            if (format == AudioFormat.Mp4)
            {
                SetTitles("MP4 (Best available)", "Video + Audio mux", "MP4 (Best available)", "Video + Audio mux",
                          "MP4 (Best available)", "Video + Audio mux", "MP4 (Best available)", "Video + Audio mux");
            }
            else
            {
                SetTitles("Low Quality", "8-bit • 22.05 kHz • Mono",
                          "Standard Quality", "16-bit • 44.1 kHz • Stereo",
                          "High Quality", "24-bit • 48 kHz • Stereo",
                          "Highest Quality", "32-bit • 96 kHz • Stereo");
            }
        }

        private void SetTitles(string lowTitle, string lowSub, string stdTitle, string stdSub, string hiTitle, string hiSub, string maxTitle, string maxSub)
        {
            if (LowTitle != null) LowTitle.Text = lowTitle;
            if (LowSubtitle != null) LowSubtitle.Text = lowSub;
            if (StandardTitle != null) StandardTitle.Text = stdTitle;
            if (StandardSubtitle != null) StandardSubtitle.Text = stdSub;
            if (HighTitle != null) HighTitle.Text = hiTitle;
            if (HighSubtitle != null) HighSubtitle.Text = hiSub;
            if (HighestTitle != null) HighestTitle.Text = maxTitle;
            if (HighestSubtitle != null) HighestSubtitle.Text = maxSub;
        }

        private async Task<(string command, string? tempCoverPath)> BuildFfmpegCommandAsync(AudioFormat format, string wavCodec, string mp3Bitrate, string outputFile)
        {
            var safeTitle = _trackName.Replace("\"", "'");
            var safeArtist = _artistName.Replace("\"", "'");
            var meta = $"-metadata title=\"{safeTitle}\" -metadata artist=\"{safeArtist}\"";

            string? tempCoverPath = null;

            if (format == AudioFormat.Mp3)
            {
                // Try to download cover art for embedding
                if (!string.IsNullOrWhiteSpace(_imageUrl))
                {
                    tempCoverPath = await DownloadCoverAsync(_imageUrl);
                }

                if (!string.IsNullOrWhiteSpace(tempCoverPath))
                {
                    var cmd = $"ffmpeg -i \"{_audioUrl}\" -i \"{tempCoverPath}\" " +
                              "-map 0:a -map 1:v " +
                              "-c:a libmp3lame -b:a " + mp3Bitrate + " " +
                              "-c:v mjpeg -id3v2_version 3 " +
                              "-metadata:s:v title=\"Album cover\" -metadata:s:v comment=\"Cover (front)\" " +
                              $"{meta} \"{outputFile}\"";
                    return (cmd, tempCoverPath);
                }
                else
                {
                    var cmd = $"ffmpeg -i \"{_audioUrl}\" -c:a libmp3lame -b:a {mp3Bitrate} {meta} \"{outputFile}\"";
                    return (cmd, null);
                }
            }
            else
            {
                var comment = string.IsNullOrWhiteSpace(_imageUrl) ? "" : $" -metadata comment=\"thumb:{_imageUrl.Replace("\"", "'")}\"";
                var cmd = $"ffmpeg -i \"{_audioUrl}\" {meta}{comment} {wavCodec} \"{outputFile}\"";
                return (cmd, null);
            }
        }

        private async Task<string> DownloadCoverAsync(string url)
        {
            try
            {
                using var client = new HttpClient();
                var bytes = await client.GetByteArrayAsync(url);
                var tempPath = Path.Combine(Path.GetTempPath(), $"sxm_cover_{Guid.NewGuid():N}.jpg");
                await File.WriteAllBytesAsync(tempPath, bytes);
                return tempPath;
            }
            catch
            {
                return null;
            }
        }

        private async Task RunM3u8WithYtDlpAsync(string baseName, AudioFormat format)
        {
            // Load bearer and key
            var bearer = await LoadBearerAsync();
            var hlsKeyHex = await LoadHlsKeyHexAsync();

            if (string.IsNullOrWhiteSpace(bearer) || string.IsNullOrWhiteSpace(hlsKeyHex))
            {
                MessageBox.Show("Missing HLS key or Authorization bearer. Ensure HLSKey files are captured.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var ext = format == AudioFormat.Mp4 ? "mp4" : "%(ext)s";
            var outputTemplate = $"{baseName}.{ext}";
            var origin = "https://www.siriusxm.com";
            var referer = "https://www.siriusxm.com";

            var cmd = new StringBuilder();
            cmd.Append("yt-dlp ");
            cmd.Append($"\"{_audioUrl}\" ");
            cmd.Append($"--add-header \"Authorization: {bearer}\" ");
            cmd.Append($"--add-header \"Origin: {origin}\" ");
            cmd.Append($"--add-header \"Referer: {referer}\" ");
            cmd.Append($"--extractor-args \"generic:hls_key={hlsKeyHex}\" ");
            cmd.Append("--downloader-args \"ffmpeg:--hls-use-mpegts\" ");
            if (format == AudioFormat.Mp4)
            {
                // Keep video; best overall; no audio extraction
                cmd.Append("-f bestvideo+bestaudio/best ");
                cmd.Append("--embed-metadata ");
                cmd.Append($"-o \"{outputTemplate}\"");
            }
            else
            {
                // Audio-only path (mp3/wav) using extraction
                cmd.Append("-f bestaudio/best ");
                cmd.Append("-x ");
                cmd.Append("--embed-metadata ");
                cmd.Append($"-o \"{outputTemplate}\"");
            }

            ExecuteProcessCommand(cmd.ToString(), null);
        }

        private async Task<string?> LoadBearerAsync()
        {
            try
            {
                var basePath = Directory.GetCurrentDirectory();
                var path = Path.Combine(basePath, "HLSKey", "authorization Bearer.txt");
                if (!File.Exists(path))
                {
                    // fallback to app base if different
                    path = Path.Combine(AppContext.BaseDirectory, "HLSKey", "authorization Bearer.txt");
                }
                if (!File.Exists(path)) return null;
                var content = await File.ReadAllTextAsync(path);
                return content.Trim();
            }
            catch
            {
                return null;
            }
        }

        private async Task<string?> LoadHlsKeyHexAsync()
        {
            try
            {
                var basePath = Directory.GetCurrentDirectory();
                var path = Path.Combine(basePath, "HLSKey", "response.json");
                if (!File.Exists(path))
                {
                    // fallback to app base if different
                    path = Path.Combine(AppContext.BaseDirectory, "HLSKey", "response.json");
                }
                if (!File.Exists(path)) return null;
                var json = await File.ReadAllTextAsync(path);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("key", out var keyProp))
                {
                    var key = keyProp.GetString();
                    return ConvertBase64ToHex(key);
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        private string? ConvertBase64ToHex(string? base64)
        {
            if (string.IsNullOrWhiteSpace(base64)) return null;
            try
            {
                var bytes = Convert.FromBase64String(base64.Trim());
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
            catch
            {
                return base64;
            }
        }

        private void ExecuteProcessCommand(string command, string? tempCoverPath = null)
        {
            var statusPath = System.IO.Path.Combine(AppContext.BaseDirectory, "download_status.txt");
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = "cmd.exe";
                // Run and close automatically when finished
                startInfo.Arguments = $"/c {command}";
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = false;
                startInfo.RedirectStandardError = false;
                startInfo.CreateNoWindow = false;

                using (Process process = Process.Start(startInfo))
                {
                    process.WaitForExit();
                    if (process.ExitCode == 0)
                    {
                        System.IO.File.WriteAllText(statusPath, "success");
                        MessageBox.Show("Download completed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        System.IO.File.WriteAllText(statusPath, "error");
                        MessageBox.Show("An error occurred during download.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                try { System.IO.File.WriteAllText(statusPath, "error"); } catch { /* ignore */ }
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Leave status file present longer so it can be detected; cleanup after 15s
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(15000);
                        if (System.IO.File.Exists(statusPath))
                        {
                            System.IO.File.Delete(statusPath);
                        }
                    }
                    catch { /* ignore cleanup errors */ }
                });

                if (!string.IsNullOrWhiteSpace(tempCoverPath) && File.Exists(tempCoverPath))
                {
                    try { File.Delete(tempCoverPath); } catch { /* ignore */ }
                }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
} 