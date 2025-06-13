using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Collections.Generic;

namespace SRXMDL.Artist
{
    public partial class Artistinfo : Window
    {
        private ArtistEntry? artistEntry;

        public Artistinfo(ArtistEntry? entry = null)
        {
            InitializeComponent();
            artistEntry = entry;
            LoadArtistInfo();
        }

        private void LoadArtistInfo()
        {
            try
            {
                // Load artist image from the ArtistEntry
                if (artistEntry != null && !string.IsNullOrEmpty(artistEntry.ThumbnailUrl))
                {
                    try
                    {
                        var bitmap = new BitmapImage(new Uri(artistEntry.ThumbnailUrl));
                        ArtistImage.Source = bitmap;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error loading image: {ex.Message}");
                    }
                }

                if (File.Exists("Artist/ArtistInfo.json"))
                {
                    var jsonContent = File.ReadAllText("Artist/ArtistInfo.json");
                    var jsonDoc = JsonDocument.Parse(jsonContent);
                    var root = jsonDoc.RootElement;

                    // Load artist basic info
                    if (root.TryGetProperty("page", out var page) && 
                        page.TryGetProperty("entity", out var entity))
                    {
                        // Set artist name
                        if (entity.TryGetProperty("texts", out var texts) &&
                            texts.TryGetProperty("title", out var title) &&
                            title.TryGetProperty("default", out var name))
                        {
                            ArtistName.Text = name.GetString();
                        }

                        // Set artist description
                        if (texts.TryGetProperty("description", out var description) &&
                            description.TryGetProperty("default", out var desc))
                        {
                            ArtistDescription.Text = desc.GetString();
                        }

                        // Load similar artists
                        if (page.TryGetProperty("decorations", out var decorations) &&
                            decorations.TryGetProperty("similarArtists", out var similarArtists) &&
                            similarArtists.ValueKind == JsonValueKind.Array)
                        {
                            var similarArtistsList = new List<string>();
                            foreach (var artist in similarArtists.EnumerateArray())
                            {
                                similarArtistsList.Add(artist.GetString());
                            }
                            StationInfo.Text = "Similar Artists:\n" + string.Join(", ", similarArtistsList);
                        }

                        // Load additional info
                        if (entity.TryGetProperty("type", out var type) &&
                            entity.TryGetProperty("id", out var id))
                        {
                            AdditionalInfo.Text = $"Type: {type.GetString()}\nID: {id.GetString()}";
                        }
                    }
                }
                else
                {
                    ArtistName.Text = "No Artist Information Available";
                    ArtistDescription.Text = "Please fetch artist information first.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading artist information: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
