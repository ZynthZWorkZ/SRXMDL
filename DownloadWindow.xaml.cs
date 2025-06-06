using System;
using System.Windows;
using System.Diagnostics;
using System.Windows.Controls;

namespace SRXMDL
{
    public partial class DownloadWindow : Window
    {
        private string _audioUrl;

        public DownloadWindow(string audioUrl)
        {
            InitializeComponent();
            _audioUrl = audioUrl;
        }

        private string GetOutputFilename(string quality)
        {
            string baseName = OutputFilenameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(baseName))
                baseName = "output";
            return $"{baseName}_{quality}.wav";
        }

        private void LowQualityDownload_Click(object sender, RoutedEventArgs e)
        {
            string outputFile = GetOutputFilename("low");
            string command = $"ffmpeg -i \"{_audioUrl}\" -acodec pcm_u8 -ar 22050 -ac 1 \"{outputFile}\"";
            ExecuteFFmpegCommand(command);
        }

        private void StandardQualityDownload_Click(object sender, RoutedEventArgs e)
        {
            string outputFile = GetOutputFilename("standard");
            string command = $"ffmpeg -i \"{_audioUrl}\" -acodec pcm_s16le -ar 44100 -ac 2 \"{outputFile}\"";
            ExecuteFFmpegCommand(command);
        }

        private void HighQualityDownload_Click(object sender, RoutedEventArgs e)
        {
            string outputFile = GetOutputFilename("high");
            string command = $"ffmpeg -i \"{_audioUrl}\" -acodec pcm_s24le -ar 48000 -ac 2 \"{outputFile}\"";
            ExecuteFFmpegCommand(command);
        }

        private void HighestQualityDownload_Click(object sender, RoutedEventArgs e)
        {
            string outputFile = GetOutputFilename("max");
            string command = $"ffmpeg -i \"{_audioUrl}\" -acodec pcm_s32le -ar 96000 -ac 2 \"{outputFile}\"";
            ExecuteFFmpegCommand(command);
        }

        private void ExecuteFFmpegCommand(string command)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = $"/c {command}";
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.CreateNoWindow = true;

                using (Process process = Process.Start(startInfo))
                {
                    process.WaitForExit();
                    if (process.ExitCode == 0)
                    {
                        MessageBox.Show("Download completed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("An error occurred during download.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
} 