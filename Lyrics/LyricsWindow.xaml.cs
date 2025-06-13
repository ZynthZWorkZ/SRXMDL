using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SRXMDL.Lyrics
{
    public partial class LyricsWindow : Window
    {
        private static LyricsWindow? _instance;

        public LyricsWindow()
        {
            InitializeComponent();
        }

        public static LyricsWindow GetInstance()
        {
            if (_instance == null || !_instance.IsLoaded)
            {
                _instance = new LyricsWindow();
            }
            return _instance;
        }

        public void SetTrackInfo(string trackName, string artistName, string? albumArtUrl = null)
        {
            try
            {
                TrackNameText.Text = trackName ?? "Unknown Track";
                ArtistNameText.Text = artistName ?? "Unknown Artist";

                if (!string.IsNullOrEmpty(albumArtUrl))
                {
                    try
                    {
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.UriSource = new Uri(albumArtUrl);
                        image.EndInit();
                        AlbumArtImage.Source = image;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error loading album art: {ex.Message}");
                        AlbumArtImage.Source = null;
                    }
                }
                else
                {
                    AlbumArtImage.Source = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in SetTrackInfo: {ex.Message}");
            }
        }

        public void SetLyrics(string lyrics)
        {
            try
            {
                LyricsText.Text = lyrics ?? "No lyrics available";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in SetLyrics: {ex.Message}");
                LyricsText.Text = "Error loading lyrics";
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            // Remove the owner relationship before closing
            Owner = null;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _instance = null;
        }
    }
} 