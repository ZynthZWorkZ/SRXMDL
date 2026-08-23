using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Serilog;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SRXMDL.Artist;

public class ArtistStations
{
    private readonly ObservableCollection<ArtistEntry> _artistEntries;
    private readonly Func<CoreWebView2?> _getCoreWebView;
    private readonly Dispatcher _dispatcher;
    private readonly string _favoritesFile = "Artist/favorites.json";
    private bool _isMonitoring;

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            _isMonitoring = value;
            foreach (var entry in _artistEntries)
                entry.CanPlay = value;
            ArtistListView?.Items.Refresh();
        }
    }

    public ListView? ArtistListView { get; set; }

    public ArtistStations(Func<CoreWebView2?> getCoreWebView, Dispatcher dispatcher, ObservableCollection<ArtistEntry> artistEntries)
    {
        _getCoreWebView = getCoreWebView;
        _dispatcher = dispatcher;
        _artistEntries = artistEntries;
        LoadFavorites();
    }

    private void LoadFavorites()
    {
        try
        {
            Directory.CreateDirectory("Artist");
            if (!File.Exists(_favoritesFile)) return;

            var favorites = JsonSerializer.Deserialize<List<ArtistEntry>>(File.ReadAllText(_favoritesFile));
            if (favorites == null) return;

            foreach (var favorite in favorites)
            {
                favorite.IsFavorite = true;
                favorite.CanPlay = IsMonitoring;
                _artistEntries.Add(favorite);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading favorites");
        }
    }

    public void SaveFavorites()
    {
        try
        {
            Directory.CreateDirectory("Artist");
            var favorites = _artistEntries
                .Where(a => a.IsFavorite)
                .GroupBy(a => a.ArtistStationUrl)
                .Select(g => g.First())
                .ToList();

            var json = JsonSerializer.Serialize(favorites, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_favoritesFile, json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving favorites");
        }
    }

    public void ToggleFavorite(Button button, ArtistEntry entry)
    {
        entry.IsFavorite = !entry.IsFavorite;
        SaveFavorites();
        UpdateFavoriteButtonAppearance(button, entry.IsFavorite);
    }

    private void UpdateFavoriteButtonAppearance(Button button, bool isFavorite)
    {
        if (isFavorite)
        {
            button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
            button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
            button.Content = "★ Favorited";
        }
        else
        {
            button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#374151"));
            button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#374151"));
            button.Content = "☆ Favorite";
        }
    }

    public async Task ProcessArtistStationUrl(string url)
    {
        var core = _getCoreWebView();
        if (core == null) return;

        try
        {
            if (!string.Equals(core.Source, url, StringComparison.OrdinalIgnoreCase))
                return;

            const string script = """
                (function() {
                    var title = document.querySelector("span[data-qa='content-page-title']");
                    var thumb = document.querySelector("span.image-module__image-inner___rZWHj img.image-module__image-image___WKoaX");
                    return JSON.stringify({
                        artist: title ? title.textContent.trim() : '',
                        thumbnail: thumb ? thumb.src : ''
                    });
                })();
                """;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var raw = await core.ExecuteScriptAsync(script);
                var json = JsonSerializer.Deserialize<string>(raw);
                if (string.IsNullOrWhiteSpace(json)) { await Task.Delay(500); continue; }

                var doc = JsonSerializer.Deserialize<JsonElement>(json);
                var artistName = doc.TryGetProperty("artist", out var a) ? a.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(artistName)) { await Task.Delay(500); continue; }

                var thumbnailUrl = doc.TryGetProperty("thumbnail", out var t) ? t.GetString() ?? "" : "";

                var existingArtist = _artistEntries.FirstOrDefault(x =>
                    x.ArtistStationUrl == url ||
                    x.Artist.Equals(artistName, StringComparison.OrdinalIgnoreCase));

                if (existingArtist == null)
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        _artistEntries.Add(new ArtistEntry
                        {
                            Artist = artistName,
                            ArtistStationUrl = url,
                            ThumbnailUrl = thumbnailUrl,
                            CanPlay = IsMonitoring
                        });
                    });
                    Log.Information("Artist station detected: {Artist} - {Url}", artistName, url);
                }
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing artist station URL: {Url}", url);
        }
    }

    public async Task PlayArtistStation(ArtistEntry entry, Action<string> updateStatus)
    {
        var core = _getCoreWebView();
        if (core == null)
        {
            updateStatus("Browser not initialized");
            return;
        }

        try
        {
            if (!string.Equals(core.Source, entry.ArtistStationUrl, StringComparison.OrdinalIgnoreCase))
                core.Navigate(entry.ArtistStationUrl);

            await Task.Delay(1500);
            await ClickPlayButtonAsync(core, entry.Artist, updateStatus);
        }
        catch (Exception ex)
        {
            updateStatus("Error playing station");
            Log.Error(ex, "Error playing artist station: {Artist}", entry.Artist);
        }
    }

    private static async Task ClickPlayButtonAsync(CoreWebView2 core, string artistName, Action<string> updateStatus)
    {
        var script = $$"""
            (function() {
                function click(btn) {
                    if (!btn) return false;
                    btn.scrollIntoView({ block: 'center' });
                    btn.click();
                    return true;
                }
                var specific = document.querySelector("button[aria-label='Play {{artistName.Replace("'", "\\'")}} Station']");
                if (click(specific)) return true;
                var generic = document.querySelector("button[aria-label*='Play']");
                return click(generic);
            })();
            """;

        for (var i = 0; i < 5; i++)
        {
            var result = await core.ExecuteScriptAsync(script);
            if (result == "true")
            {
                updateStatus($"Playing {artistName} station...");
                Log.Information("Started playing artist station: {Artist}", artistName);
                return;
            }
            await Task.Delay(600);
        }

        throw new InvalidOperationException("Could not find artist play button");
    }

    public void SetMonitoringStatus(bool isActive) => IsMonitoring = isActive;
}

public class ArtistEntry
{
    public string Artist { get; set; } = string.Empty;
    public string ArtistStationUrl { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public bool CanPlay { get; set; }
}
