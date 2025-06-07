using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using Serilog.Events;
using System.Windows;
using System.Windows.Threading;
using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Linq;

namespace SRXMDL;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private ChromeDriver? driver;
    private CancellationTokenSource? cancellationTokenSource;
    private bool _isMonitoring = false;
    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            _isMonitoring = value;
            // Update CanPlay for all artist entries
            foreach (var entry in artistEntries)
            {
                entry.CanPlay = value;
            }
            ArtistListView.Items.Refresh();
        }
    }
    private ObservableCollection<StreamEntry> streamEntries;
    private ObservableCollection<ArtistEntry> artistEntries;
    private const string FAVORITES_FILE = "favorites.json";

    public MainWindow()
    {
        InitializeComponent();
        streamEntries = new ObservableCollection<StreamEntry>();
        artistEntries = new ObservableCollection<ArtistEntry>();
        StreamListView.ItemsSource = streamEntries;
        ArtistListView.ItemsSource = artistEntries;
        SetupLogging();
        LoadFavorites();
    }

    private void SetupLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File("mp4_requests.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is StreamEntry entry)
        {
            Clipboard.SetText(entry.Url);
            StatusText.Text = "URL copied to clipboard!";
        }
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is StreamEntry entry)
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffplay",
                    Arguments = $"\"{entry.Url}\"",
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(startInfo);
                StatusText.Text = "Playing media...";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error playing media");
                StatusText.Text = "Error playing media. Make sure ffplay is installed.";
            }
        }
    }

    private void LoadFavorites()
    {
        try
        {
            if (File.Exists(FAVORITES_FILE))
            {
                var favorites = JsonSerializer.Deserialize<List<ArtistEntry>>(File.ReadAllText(FAVORITES_FILE));
                if (favorites != null)
                {
                    foreach (var favorite in favorites)
                    {
                        favorite.IsFavorite = true;
                        favorite.CanPlay = IsMonitoring;
                        artistEntries.Add(favorite);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading favorites");
        }
    }

    private void SaveFavorites()
    {
        try
        {
            var favorites = artistEntries.Where(a => a.IsFavorite).ToList();
            var json = JsonSerializer.Serialize(favorites, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FAVORITES_FILE, json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving favorites");
        }
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is ArtistEntry entry)
        {
            entry.IsFavorite = !entry.IsFavorite;
            SaveFavorites();
            
            // Update button appearance
            UpdateFavoriteButtonAppearance(button, entry.IsFavorite);
        }
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

    private void UpdateMonitoringStatus(bool isActive)
    {
        Dispatcher.Invoke(() =>
        {
            IsMonitoring = isActive;
            if (isActive)
            {
                StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")); // AccentGreen
                StatusIndicator.Effect = new DropShadowEffect
                {
                    Color = (Color)ColorConverter.ConvertFromString("#10B981"),
                    Opacity = 0.6,
                    BlurRadius = 4,
                    ShadowDepth = 0
                };
                ConnectionStatus.Text = "Active";
                
                // Start blinking animation
                var blinkAnimation = (Storyboard)FindResource("BlinkAnimation");
                blinkAnimation.Begin(StatusIndicator);
            }
            else
            {
                StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")); // AccentRed
                StatusIndicator.Effect = new DropShadowEffect
                {
                    Color = (Color)ColorConverter.ConvertFromString("#EF4444"),
                    Opacity = 0.6,
                    BlurRadius = 4,
                    ShadowDepth = 0
                };
                ConnectionStatus.Text = "Not Active";
                
                // Stop any running animation
                StatusIndicator.BeginAnimation(UIElement.OpacityProperty, null);
                StatusIndicator.Opacity = 1;
            }
        });
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsMonitoring) return;

        try
        {
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusText.Text = "Starting...";
            IsMonitoring = true;
            streamEntries.Clear();
            
            // Instead of clearing all entries, only clear non-favorites
            var nonFavorites = artistEntries.Where(a => !a.IsFavorite).ToList();
            foreach (var entry in nonFavorites)
            {
                artistEntries.Remove(entry);
            }
            
            UpdateMonitoringStatus(true);

            cancellationTokenSource = new CancellationTokenSource();

            // Handle cookie management
            await HandleCookieManagement();

            // Initialize Chrome options
            var options = new ChromeOptions();
            options.AddArgument("--start-maximized");
            options.SetLoggingPreference(LogType.Performance, LogLevel.All);

            // Initialize Chrome driver
            driver = new ChromeDriver(options);

            // First navigate to the domain to set cookies
            await NavigateWithRetry(driver, "https://www.siriusxm.com");

            // Check for welcome page redirect
            if (driver.Url == "https://www.siriusxm.com/player/welcome")
            {
                await StopMonitoring();
                MessageBox.Show(
                    "⚠️ Cookie Update Required ⚠️\n\n" +
                    "Please follow these steps:\n" +
                    "1. Visit SiriusXM website\n" +
                    "2. Log in to your account\n" +
                    "3. Export your cookies\n" +
                    "4. Save them to /cookies as temp.json\n\n" +
                    "This will automatically update your cookie values.",
                    "Cookie Update Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            // Read cookies from file
            var cookieJson = await File.ReadAllTextAsync("cookies/www.siriusxm.com.json");
            var cookieData = JsonSerializer.Deserialize<CookieData>(cookieJson);

            // Add each cookie to the browser
            foreach (var cookie in cookieData.Cookies)
            {
                try
                {
                    var seleniumCookie = new Cookie(
                        cookie.Name,
                        cookie.Value,
                        cookie.Domain,
                        cookie.Path,
                        cookie.ExpirationDate != null ? DateTimeOffset.FromUnixTimeSeconds((long)cookie.ExpirationDate).DateTime : null
                    );

                    driver.Manage().Cookies.AddCookie(seleniumCookie);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error adding cookie {CookieName}", cookie.Name);
                }
            }

            // Navigate to the player home page
            await NavigateWithRetry(driver, "https://www.siriusxm.com/player/home");

            // Check again for welcome page redirect after navigation
            if (driver.Url == "https://www.siriusxm.com/player/welcome")
            {
                await StopMonitoring();
                MessageBox.Show(
                    "⚠️ Cookie Update Required ⚠️\n\n" +
                    "Please follow these steps:\n" +
                    "1. Visit SiriusXM website\n" +
                    "2. Log in to your account\n" +
                    "3. Export your cookies\n" +
                    "4. Save them to /cookies as temp.json\n\n" +
                    "This will automatically update your cookie values.",
                    "Cookie Update Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            StatusText.Text = "Monitoring active";
            Log.Information("Browser is now open and monitoring for MP4 and streaming requests.");

            // Start monitoring in background
            _ = Task.Run(() => MonitorNetworkTraffic(cancellationTokenSource.Token));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred while starting the monitor");
            StatusText.Text = "Error occurred";
            await StopMonitoring();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopMonitoring();
    }

    private async Task StopMonitoring()
    {
        if (!IsMonitoring) return;

        try
        {
            cancellationTokenSource?.Cancel();
            driver?.Quit();
            driver?.Dispose();
            driver = null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while stopping monitoring");
        }
        finally
        {
            IsMonitoring = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StatusText.Text = "Ready";
            UpdateMonitoringStatus(false);
        }
    }

    private async Task MonitorNetworkTraffic(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Check if we've been redirected to the welcome page
                if (driver?.Url != null && driver.Url.Contains("siriusxm.com/player/welcome"))
                {
                    Log.Warning("Detected welcome page redirect");
                    await StopMonitoring();
                    
                    // Ensure we're on the UI thread and show the message
                    await Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            MessageBox.Show(
                                "⚠️ Cookie Update Required ⚠️\n\n" +
                                "Please follow these steps:\n" +
                                "1. Visit SiriusXM website\n" +
                                "2. Log in to your account\n" +
                                "3. Export your cookies\n" +
                                "4. Save them to /cookies as temp.json\n\n" +
                                "This will automatically update your cookie values.",
                                "Cookie Update Required",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning
                            );
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error showing message box");
                        }
                    });
                    return;
                }

                // Check current URL for artist station
                if (driver?.Url != null && driver.Url.StartsWith("https://www.siriusxm.com/player/artist-station"))
                {
                    await ProcessArtistStationUrl(driver.Url);
                }

                var logs = driver?.Manage().Logs.GetLog(LogType.Performance);
                if (logs != null)
                {
                    foreach (var log in logs)
                    {
                        try
                        {
                            var logEntry = JsonSerializer.Deserialize<PerformanceLogEntry>(log.Message);
                            if (logEntry?.Message?.Method == "Network.responseReceived")
                            {
                                var url = logEntry.Message.Params?.Response?.Url;
                                if (url != null)
                                {
                                    // Check for artist station URLs
                                    if (url.StartsWith("https://www.siriusxm.com/player/artist-station"))
                                    {
                                        await ProcessArtistStationUrl(url);
                                    }

                                    // Skip image traffic and imgsrv-sxm domain
                                    if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                        url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                        url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                        url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                                        url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                                        url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                                        url.Contains("imgsrv-sxm") ||
                                        url.Contains("lookaround-cache-prod.streaming.siriusxm.com"))
                                    {
                                        continue;
                                    }

                                    // Monitor streaming traffic
                                    if (url.Contains(".m3u8") ||
                                        url.Contains(".mp3") ||
                                        url.Contains(".mp4") ||
                                        url.Contains("aod-akc-prod-device.streaming.siriusxm.com") ||
                                        url.Contains("streaming.siriusxm.com"))
                                    {
                                        string streamType = "unknown";
                                        if (url.Contains(".m3u8")) streamType = "m3u8";
                                        else if (url.Contains(".mp3")) streamType = "mp3";
                                        else if (url.Contains(".mp4")) streamType = "mp4";
                                        else if (url.Contains("aod-akc-prod-device.streaming.siriusxm.com")) streamType = "aod";
                                        else if (url.Contains("streaming.siriusxm.com")) streamType = "stream";

                                        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                                        Log.Information("Stream Request detected [{StreamType}]: {Url}", streamType, url);
                                        await File.AppendAllTextAsync("stream_requests.txt", $"{timestamp} [{streamType}]: {url}\n");

                                        // Add to ListView
                                        Dispatcher.Invoke(() =>
                                        {
                                            streamEntries.Add(new StreamEntry
                                            {
                                                Timestamp = timestamp,
                                                StreamType = streamType,
                                                Url = url
                                            });
                                            // Update total count
                                            TotalCapturedCount.Text = streamEntries.Count.ToString();
                                        });
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "Error processing log entry");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in monitoring loop");
            }

            await Task.Delay(1000, cancellationToken);
        }
    }

    private async Task HandleCookieManagement()
    {
        try
        {
            if (File.Exists("cookies/temp.json"))
            {
                Log.Information("Found temp.json, updating main cookie file...");

                var tempJson = await File.ReadAllTextAsync("cookies/temp.json");
                var mainJson = await File.ReadAllTextAsync("cookies/www.siriusxm.com.json");

                var tempData = JsonSerializer.Deserialize<CookieData>(tempJson);
                var mainData = JsonSerializer.Deserialize<CookieData>(mainJson);

                var tempCookies = tempData.Cookies.ToDictionary(c => c.Name);

                foreach (var cookie in mainData.Cookies)
                {
                    if (tempCookies.TryGetValue(cookie.Name, out var tempCookie))
                    {
                        cookie.Value = tempCookie.Value;
                    }
                }

                var updatedJson = JsonSerializer.Serialize(mainData, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync("cookies/www.siriusxm.com.json", updatedJson);

                File.Delete("cookies/temp.json");
                Log.Information("Cookie update completed and temp.json deleted");
            }
            else if (File.Exists("cookies/temp.txt"))
            {
                Log.Information("Found temp.txt, updating main cookie file...");

                var tempContent = await File.ReadAllTextAsync("cookies/temp.txt");
                var tempData = ParseNetscapeCookieFile(tempContent);
                var mainJson = await File.ReadAllTextAsync("cookies/www.siriusxm.com.json");
                var mainData = JsonSerializer.Deserialize<CookieData>(mainJson);

                // Create dictionary with the last occurrence of each cookie
                var tempCookies = new Dictionary<string, CookieInfo>();
                foreach (var cookie in tempData.Cookies)
                {
                    tempCookies[cookie.Name] = cookie; // This will overwrite any previous cookie with the same name
                }

                // Update values in mainData
                foreach (var cookie in mainData.Cookies)
                {
                    if (tempCookies.TryGetValue(cookie.Name, out var tempCookie))
                    {
                        cookie.Value = tempCookie.Value;
                        // Also update other properties if they exist in the temp cookie
                        if (tempCookie.ExpirationDate.HasValue)
                            cookie.ExpirationDate = tempCookie.ExpirationDate;
                        if (!string.IsNullOrEmpty(tempCookie.Path))
                            cookie.Path = tempCookie.Path;
                        cookie.Secure = tempCookie.Secure;
                        cookie.HttpOnly = tempCookie.HttpOnly;
                    }
                }

                var updatedJson = JsonSerializer.Serialize(mainData, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync("cookies/www.siriusxm.com.json", updatedJson);

                File.Delete("cookies/temp.txt");
                Log.Information("Cookie update completed and temp.txt deleted");
            }
            else
            {
                Log.Information("No temp file found, using existing cookie file");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during cookie management");
            throw;
        }
    }

    private CookieData ParseNetscapeCookieFile(string content)
    {
        var cookieData = new CookieData
        {
            Url = "https://www.siriusxm.com",
            Cookies = new List<CookieInfo>()
        };

        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        // Skip the header line if it exists
        int startIndex = lines[0].StartsWith("#") ? 1 : 0;

        for (int i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

            var parts = line.Split('\t');
            if (parts.Length >= 7)
            {
                var cookie = new CookieInfo
                {
                    Domain = parts[0],
                    HttpOnly = false,
                    Path = parts[2],
                    Secure = parts[3] == "TRUE",
                    ExpirationDate = double.Parse(parts[4]),
                    Name = parts[5],
                    Value = parts[6],
                    HostOnly = false,
                    SameSite = "no_restriction",
                    Session = false,
                    StoreId = "0"
                };

                cookieData.Cookies.Add(cookie);
            }
        }

        return cookieData;
    }

    private async Task NavigateWithRetry(IWebDriver driver, string url, int maxRetries = 3)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                driver.Navigate().GoToUrl(url);
                await Task.Delay(2000);
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Navigation attempt {Attempt} failed for URL: {Url}", i + 1, url);
                if (i == maxRetries - 1)
                    throw;
                await Task.Delay(2000 * (i + 1));
            }
        }
    }

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        var item = button.DataContext as StreamEntry;
        if (item != null)
        {
            var downloadWindow = new DownloadWindow(item.Url);
            downloadWindow.Owner = this;
            downloadWindow.ShowDialog();
        }
    }

    private async void ResetCookiesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cookiePath = "cookies/www.siriusxm.com.json";
            if (File.Exists(cookiePath))
            {
                var cookieJson = await File.ReadAllTextAsync(cookiePath);
                var cookieData = JsonSerializer.Deserialize<CookieData>(cookieJson);

                // Reset all cookie values to "0000"
                foreach (var cookie in cookieData.Cookies)
                {
                    cookie.Value = "0000";
                }

                // Save the updated cookies
                var updatedJson = JsonSerializer.Serialize(cookieData, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(cookiePath, updatedJson);

                StatusText.Text = "Cookies have been reset successfully";
                Log.Information("Cookies have been reset successfully");
            }
            else
            {
                StatusText.Text = "Cookie file not found";
                Log.Warning("Cookie file not found at {Path}", cookiePath);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error resetting cookies";
            Log.Error(ex, "Error resetting cookies");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        StopMonitoring().Wait();
        Log.CloseAndFlush();
    }

    private async Task ProcessArtistStationUrl(string url)
    {
        try
        {
            // Wait for the page to load and find the title element
            if (driver != null)
            {
                try
                {
                    // Wait for the title element to be present
                    var wait = new OpenQA.Selenium.Support.UI.WebDriverWait(driver, TimeSpan.FromSeconds(10));
                    var titleElement = wait.Until(d => d.FindElement(By.CssSelector("span[data-qa='content-page-title']")));
                    
                    var artistName = titleElement.Text.Trim();
                    
                    // Find the thumbnail image
                    string thumbnailUrl = "";
                    try
                    {
                        var thumbnailElement = driver.FindElement(By.CssSelector("span.image-module__image-inner___rZWHj img.image-module__image-image___WKoaX"));
                        thumbnailUrl = thumbnailElement.GetAttribute("src");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Could not find thumbnail image for artist: {Artist}", artistName);
                    }
                    
                    // Check if this artist is already in the list
                    var existingArtist = artistEntries.FirstOrDefault(a => a.ArtistStationUrl == url);
                    if (existingArtist == null)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            artistEntries.Add(new ArtistEntry
                            {
                                Artist = artistName,
                                ArtistStationUrl = url,
                                ThumbnailUrl = thumbnailUrl,
                                CanPlay = IsMonitoring
                            });
                        });
                        Log.Information("Artist station detected: {Artist} - {Url} - Thumbnail: {Thumbnail}", artistName, url, thumbnailUrl);
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

    private async void PlayArtistStation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is ArtistEntry entry)
        {
            try
            {
                if (driver != null)
                {
                    // If we're already on the correct page, just click play
                    if (driver.Url == entry.ArtistStationUrl)
                    {
                        await ClickPlayButton(entry.Artist);
                        return;
                    }

                    // Navigate to the artist station URL
                    driver.Navigate().GoToUrl(entry.ArtistStationUrl);
                    
                    // Wait for the play button to be present and click it
                    await ClickPlayButton(entry.Artist);
                }
                else
                {
                    StatusText.Text = "Browser not initialized";
                    Log.Warning("Attempted to play artist station but browser was not initialized");
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error playing station";
                Log.Error(ex, "Error playing artist station: {Artist}", entry.Artist);
            }
        }
    }

    private async Task ClickPlayButton(string artistName)
    {
        try
        {
            var wait = new OpenQA.Selenium.Support.UI.WebDriverWait(driver, TimeSpan.FromSeconds(5));
            
            // Try to find the play button with the specific artist name first
            try
            {
                var playButton = wait.Until(d => d.FindElement(By.CssSelector($"button[aria-label='Play {artistName} Station']")));
                ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView(true);", playButton);
                playButton.Click();
            }
            catch (OpenQA.Selenium.WebDriverTimeoutException)
            {
                // If specific button not found, try the generic play button
                Log.Warning("Specific play button not found for {Artist}, trying generic play button", artistName);
                var playButton = wait.Until(d => d.FindElement(By.CssSelector("button[aria-label*='Play']")));
                ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView(true);", playButton);
                playButton.Click();
            }
            
            StatusText.Text = $"Playing {artistName} station...";
            Log.Information("Started playing artist station: {Artist}", artistName);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to click play button: {ex.Message}", ex);
        }
    }
}

// Classes to deserialize the cookie JSON
public class CookieData
{
    [JsonPropertyName("url")]
    public string Url { get; set; }

    [JsonPropertyName("cookies")]
    public List<CookieInfo> Cookies { get; set; }
}

public class CookieInfo
{
    [JsonPropertyName("domain")]
    public string Domain { get; set; }

    [JsonPropertyName("expirationDate")]
    public double? ExpirationDate { get; set; }

    [JsonPropertyName("hostOnly")]
    public bool HostOnly { get; set; }

    [JsonPropertyName("httpOnly")]
    public bool HttpOnly { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; }

    [JsonPropertyName("sameSite")]
    public string SameSite { get; set; }

    [JsonPropertyName("secure")]
    public bool Secure { get; set; }

    [JsonPropertyName("session")]
    public bool Session { get; set; }

    [JsonPropertyName("storeId")]
    public string StoreId { get; set; }

    [JsonPropertyName("value")]
    public string Value { get; set; }
}

// Classes to deserialize Chrome performance logs
public class PerformanceLogEntry
{
    [JsonPropertyName("message")]
    public PerformanceMessage Message { get; set; }
}

public class PerformanceMessage
{
    [JsonPropertyName("method")]
    public string Method { get; set; }

    [JsonPropertyName("params")]
    public PerformanceParams Params { get; set; }
}

public class PerformanceParams
{
    [JsonPropertyName("response")]
    public PerformanceResponse Response { get; set; }
}

public class PerformanceResponse
{
    [JsonPropertyName("url")]
    public string Url { get; set; }
}

public class StreamEntry
{
    public string Timestamp { get; set; }
    public string StreamType { get; set; }
    public string Url { get; set; }
    public bool CanPlay => StreamType == "mp4" || StreamType == "mp3" || StreamType == "m3u8";
}

public class ArtistEntry
{
    public string Artist { get; set; }
    public string ArtistStationUrl { get; set; }
    public string ThumbnailUrl { get; set; }
    public bool IsFavorite { get; set; }
    public bool CanPlay { get; set; }
}