using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;

namespace SRXMDL.Artist;

public class ArtistStations
{
    private readonly ObservableCollection<ArtistEntry> _artistEntries;
    private readonly ChromeDriver? _driver;
    private readonly Dispatcher _dispatcher;
    private readonly string _favoritesFile = "Artist/favorites.json";
    private bool _isMonitoring;

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            _isMonitoring = value;
            // Update CanPlay for all artist entries
            foreach (var entry in _artistEntries)
            {
                entry.CanPlay = value;
            }
            ArtistListView?.Items.Refresh();
        }
    }

    public ListView? ArtistListView { get; set; }

    public ArtistStations(ChromeDriver? driver, Dispatcher dispatcher, ObservableCollection<ArtistEntry> artistEntries)
    {
        _driver = driver;
        _dispatcher = dispatcher;
        _artistEntries = artistEntries;
        LoadFavorites();
    }

    private void LoadFavorites()
    {
        try
        {
            // Ensure the Artist directory exists
            Directory.CreateDirectory("Artist");
            
            if (File.Exists(_favoritesFile))
            {
                var favorites = JsonSerializer.Deserialize<List<ArtistEntry>>(File.ReadAllText(_favoritesFile));
                if (favorites != null)
                {
                    foreach (var favorite in favorites)
                    {
                        favorite.IsFavorite = true;
                        favorite.CanPlay = IsMonitoring;
                        _artistEntries.Add(favorite);
                    }
                }
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
            // Ensure the Artist directory exists
            Directory.CreateDirectory("Artist");
            
            // Get unique favorites by URL to prevent duplicates
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
            button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")); // Yellow
            button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
            button.Content = "★ Favorited";
        }
        else
        {
            button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#374151")); // Default
            button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#374151"));
            button.Content = "☆ Favorite";
        }
    }

    public async Task ProcessArtistStationUrl(string url)
    {
        try
        {
            if (_driver != null)
            {
                try
                {
                    var wait = new OpenQA.Selenium.Support.UI.WebDriverWait(_driver, TimeSpan.FromSeconds(10));
                    var titleElement = wait.Until(d => d.FindElement(By.CssSelector("span[data-qa='content-page-title']")));
                    
                    var artistName = titleElement.Text.Trim();
                    
                    string thumbnailUrl = "";
                    try
                    {
                        var thumbnailElement = _driver.FindElement(By.CssSelector("span.image-module__image-inner___rZWHj img.image-module__image-image___WKoaX"));
                        thumbnailUrl = thumbnailElement.GetAttribute("src");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Could not find thumbnail image for artist: {Artist}", artistName);
                    }
                    
                    // Check for existing artist by both URL and name to prevent duplicates
                    var existingArtist = _artistEntries.FirstOrDefault(a => 
                        a.ArtistStationUrl == url || 
                        a.Artist.Equals(artistName, StringComparison.OrdinalIgnoreCase));
                        
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
                        Log.Information("Artist station detected: {Artist} - {Url} - Thumbnail: {Thumbnail}", artistName, url, thumbnailUrl);
                    }
                    else
                    {
                        Log.Information("Duplicate artist station skipped: {Artist} - {Url}", artistName, url);
                    }
                }
                catch (OpenQA.Selenium.WebDriverTimeoutException)
                {
                    Log.Warning("Timeout waiting for artist title element on page: {Url}", url);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error finding artist title element on page: {Url}", url);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing artist station URL: {Url}", url);
        }
    }

    public async Task PlayArtistStation(ArtistEntry entry, Action<string> updateStatus)
    {
        try
        {
            if (_driver != null)
            {
                if (_driver.Url == entry.ArtistStationUrl)
                {
                    await ClickPlayButton(entry.Artist, updateStatus);
                    return;
                }

                _driver.Navigate().GoToUrl(entry.ArtistStationUrl);
                await ClickPlayButton(entry.Artist, updateStatus);
            }
            else
            {
                updateStatus("Browser not initialized");
                Log.Warning("Attempted to play artist station but browser was not initialized");
            }
        }
        catch (Exception ex)
        {
            updateStatus("Error playing station");
            Log.Error(ex, "Error playing artist station: {Artist}", entry.Artist);
        }
    }

    private async Task ClickPlayButton(string artistName, Action<string> updateStatus)
    {
        try
        {
            var wait = new OpenQA.Selenium.Support.UI.WebDriverWait(_driver, TimeSpan.FromSeconds(5));
            
            try
            {
                var playButton = wait.Until(d => d.FindElement(By.CssSelector($"button[aria-label='Play {artistName} Station']")));
                ((IJavaScriptExecutor)_driver).ExecuteScript("arguments[0].scrollIntoView(true);", playButton);
                playButton.Click();
            }
            catch (OpenQA.Selenium.WebDriverTimeoutException)
            {
                Log.Warning("Specific play button not found for {Artist}, trying generic play button", artistName);
                var playButton = wait.Until(d => d.FindElement(By.CssSelector("button[aria-label*='Play']")));
                ((IJavaScriptExecutor)_driver).ExecuteScript("arguments[0].scrollIntoView(true);", playButton);
                playButton.Click();
            }
            
            updateStatus($"Playing {artistName} station...");
            Log.Information("Started playing artist station: {Artist}", artistName);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to click play button: {ex.Message}", ex);
        }
    }

    public void SetMonitoringStatus(bool isActive)
    {
        IsMonitoring = isActive;
    }
}

public class ArtistEntry
{
    public string Artist { get; set; }
    public string ArtistStationUrl { get; set; }
    public string ThumbnailUrl { get; set; }
    public bool IsFavorite { get; set; }
    public bool CanPlay { get; set; }
}
