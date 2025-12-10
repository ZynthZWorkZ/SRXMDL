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
using System.Windows.Media.Imaging;
using System.Threading;
using System.Threading.Tasks;
using OpenQA.Selenium.Support.UI;
using System.Net.Http;
using System.Windows.Shapes;
using System.Windows.Input;
using SRXMDL.Artist;
using SRXMDL.Download;
using SRXMDL.Lyrics;

namespace SRXMDL;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private ChromeDriver? driver;
    private CancellationTokenSource? cancellationTokenSource;
    private bool _isMonitoring = false;
    private bool _isPaused = false;
    private NowPlaying currentTrack;
    private DispatcherTimer nowPlayingTimer;
    private FileSystemWatcher? stationFeedbackWatcher;
    private string? lastTuneSourceUrl;
    private string? lastTuneSourcePayload;
    private string? lastTuneSourceAuthToken;
    private ArtistStations? artistStations;
    private const double BaseWidth = 1250;
    private const double BaseHeight = 800;
    private const double MinScale = 0.7;
    private const double MaxScale = 1.0;
    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            _isMonitoring = value;
            artistStations?.SetMonitoringStatus(value);
        }
    }
    private ObservableCollection<StreamEntry> streamEntries;
    private ObservableCollection<Artist.ArtistEntry> artistEntries;
    private const string FAVORITES_FILE = "favorites.json";

    public MainWindow()
    {
        InitializeComponent();
        streamEntries = new ObservableCollection<StreamEntry>();
        artistEntries = new ObservableCollection<Artist.ArtistEntry>();
        StreamListView.ItemsSource = streamEntries;
        ((ListView)FindName("ArtistListView")).ItemsSource = artistEntries;
        currentTrack = new NowPlaying();
        SetupLogging();
        SetupNowPlayingTimer();
        SetupStationFeedbackWatcher();
        UpdateResponsiveLayout();
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

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is Artist.ArtistEntry entry)
        {
            artistStations?.ToggleFavorite(button, entry);
        }
    }

    private void SetupNowPlayingTimer()
    {
        nowPlayingTimer = new DispatcherTimer();
        nowPlayingTimer.Interval = TimeSpan.FromSeconds(2);
        nowPlayingTimer.Tick += async (s, e) => await UpdateNowPlaying();
    }

    private async Task UpdateNowPlaying()
    {
        if (driver == null || !IsMonitoring) return;

        try
        {
            var wait = new OpenQA.Selenium.Support.UI.WebDriverWait(driver, TimeSpan.FromSeconds(5));
            
            // Get track name
            string newTrackName = "No track playing";
            try
            {
                var trackElement = wait.Until(d => d.FindElement(By.CssSelector("div.styles-module__title___D3wQt")));
                newTrackName = trackElement.Text.Trim();
                
                // Skip if the new track name is exactly the same as the current one
                if (newTrackName == currentTrack.TrackName)
                {
                    return;
                }
            }
            catch
            {
                // Keep existing track name if we can't get a new one
                newTrackName = currentTrack.TrackName ?? "No track playing";
            }

            // Get station name
            string newStationName = "No station selected";
            try
            {
                var stationElement = wait.Until(d => d.FindElement(By.CssSelector("div.styles-module__text___xT9yv span")));
                newStationName = stationElement.Text.Trim();
            }
            catch
            {
                // Keep existing station name if we can't get a new one
                newStationName = currentTrack.StationName ?? "No station selected";
            }

            // Get album art
            string newAlbumArtUrl = "";
            try
            {
                var albumArtElement = wait.Until(d => d.FindElement(By.CssSelector("div.styles-module__imageContainer___b-ipU img")));
                newAlbumArtUrl = albumArtElement.GetAttribute("src");
                Log.Debug("Found album art URL: {Url}", newAlbumArtUrl);
            }
            catch (Exception ex)
            {
                // Keep existing album art URL if we can't get a new one
                newAlbumArtUrl = currentTrack.AlbumArtUrl ?? "";
                Log.Debug(ex, "Could not find album art element");
            }

            // Only update UI if values have changed
            await Dispatcher.InvokeAsync(() =>
            {
                if (newTrackName != currentTrack.TrackName)
                {
                    currentTrack.TrackName = newTrackName;
                    NowPlayingTrack.Text = newTrackName;
                    Log.Debug("Updated track name to: {TrackName}", newTrackName);
                }

                if (newStationName != currentTrack.StationName)
                {
                    currentTrack.StationName = newStationName;
                    NowPlayingStation.Text = newStationName;
                    Log.Debug("Updated station name to: {StationName}", newStationName);
                }

                if (newAlbumArtUrl != currentTrack.AlbumArtUrl)
                {
                    currentTrack.AlbumArtUrl = newAlbumArtUrl;
                    if (!string.IsNullOrEmpty(newAlbumArtUrl))
                    {
                        try
                        {
                            NowPlayingArt.Source = new BitmapImage(new Uri(newAlbumArtUrl));
                            Log.Debug("Updated album art image");
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error loading album art image from URL: {Url}", newAlbumArtUrl);
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error updating now playing information");
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

                // Start now playing updates
                nowPlayingTimer.Start();
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

                // Stop now playing updates
                nowPlayingTimer.Stop();
                
                // Clear now playing information
                NowPlayingTrack.Text = "No track playing";
                NowPlayingStation.Text = "No station selected";
                NowPlayingArt.Source = null;
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

            // Skip cookie management - user will login manually

            // Initialize Chrome options
            var options = new ChromeOptions();
            options.AddArgument("--start-maximized");
            options.SetLoggingPreference(LogType.Performance, LogLevel.All);

            // Initialize Chrome driver
            driver = new ChromeDriver(options);
            
            // Initialize ArtistStations
            artistStations = new ArtistStations(driver, Dispatcher, artistEntries);
            artistStations.ArtistListView = ArtistListView;

            // Navigate directly to the player page for manual login
            await NavigateWithRetry(driver, "https://www.siriusxm.com/player/home");

            StatusText.Text = "Browser opened - please login manually";
            Log.Information("Browser is now open. User can login manually and monitoring will begin automatically.");

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

            // Clear all files in the Stations directory
            if (Directory.Exists("Stations"))
            {
                foreach (var file in Directory.GetFiles("Stations"))
                {
                    try
                    {
                        File.Delete(file);
                        Log.Information("Deleted file: {File}", file);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error deleting file: {File}", file);
                    }
                }
            }
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

    private string ExtractFileNameFromUrl(string url)
    {
        try
        {
            // Extract the last part of the URL after the last '/'
            var fileName = url.Split('/').Last();
            // Remove any query parameters if present
            fileName = fileName.Split('?')[0];
            return fileName;
        }
        catch
        {
            return url; // Return original URL if parsing fails
        }
    }

    private bool IsDuplicateStream(string url, out StreamEntry existingEntry)
    {
        var fileName = ExtractFileNameFromUrl(url);
        existingEntry = streamEntries.FirstOrDefault(entry => ExtractFileNameFromUrl(entry.Url) == fileName);
        return existingEntry != null;
    }

    private async Task MonitorNetworkTraffic(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Check if we're on the welcome page (user needs to login)
                if (driver?.Url != null && driver.Url.Contains("siriusxm.com/player/welcome"))
                {
                    Log.Information("User is on welcome page - waiting for manual login");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        StatusText.Text = "Please login in the browser window";
                    });
                }

                // Check if user has successfully logged in (not on welcome page)
                if (driver?.Url != null && !driver.Url.Contains("siriusxm.com/player/welcome") && driver.Url.Contains("siriusxm.com"))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        StatusText.Text = "Monitoring active - login successful";
                    });
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

                                    // Check for MP3 traffic
                                    if (url.Contains(".mp3", StringComparison.OrdinalIgnoreCase))
                                    {
                                        StreamEntry existingEntry;
                                        // Check if this is a duplicate file
                                        if (IsDuplicateStream(url, out existingEntry))
                                        {
                                            Log.Debug("Skipping duplicate MP3 file: {Url}", url);
                                            continue;
                                        }

                                        Log.Information("MP3 traffic detected: {Url}", url);
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            streamEntries.Add(new StreamEntry
                                            {
                                                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                                                StreamType = "mp3",
                                                Url = url,
                                                TrackName = "Unnamed MP3",
                                                ArtistName = "Unknown",
                                                PreferredImageUrl = null
                                            });
                                            UpdateTotalCapturedCount();
                                        });
                                        continue;
                                    }

                                    // Check for unnamed MP4 traffic
                                    if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && !url.Contains("named"))
                                    {
                                        StreamEntry existingEntry;
                                        // Check if this is a duplicate file
                                        if (IsDuplicateStream(url, out existingEntry))
                                        {
                                            Log.Debug("Skipping duplicate MP4 file: {Url}", url);
                                            continue;
                                        }

                                        Log.Information("Unnamed MP4 traffic detected: {Url}", url);
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            streamEntries.Add(new StreamEntry
                                            {
                                                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                                                StreamType = "mp4",
                                                Url = url,
                                                TrackName = "Unnamed MP4",
                                                ArtistName = "Unknown",
                                                PreferredImageUrl = null
                                            });
                                            UpdateTotalCapturedCount();
                                        });
                                        continue;
                                    }

                                    // Check for M3U8 traffic
                                    if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                                    {
                                        StreamEntry existingEntry;
                                        // Check if this is a duplicate file
                                        if (IsDuplicateStream(url, out existingEntry))
                                        {
                                            Log.Debug("Skipping duplicate M3U8 file: {Url}", url);
                                            continue;
                                        }

                                        Log.Information("M3U8 traffic detected: {Url}", url);
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            streamEntries.Add(new StreamEntry
                                            {
                                                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                                                StreamType = "m3u8",
                                                Url = url,
                                                TrackName = "Unnamed M3U8",
                                                ArtistName = "Unknown",
                                                PreferredImageUrl = null
                                            });
                                            UpdateTotalCapturedCount();
                                        });
                                        continue;
                                    }

                                    // Monitor station feedback endpoint
                                    if (url.Contains("api.edge-gateway.siriusxm.com/stations/v1/station-feedback/station/type/artist-station/id"))
                                    {
                                        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                                        Log.Information("Station feedback request detected: {Url}", url);

                                        // Get the auth token from the original request
                                        var feedbackAuthToken = "";

                                        var feedbackRequestLog = logs.FirstOrDefault(l =>
                                        {
                                            var entry = JsonSerializer.Deserialize<PerformanceLogEntry>(l.Message);
                                            return entry?.Message?.Method == "Network.requestWillBeSent" &&
                                                   entry?.Message?.Params?.RequestId == logEntry.Message.Params?.RequestId;
                                        });

                                        if (feedbackRequestLog != null)
                                        {
                                            var requestEntry = JsonSerializer.Deserialize<PerformanceLogEntry>(feedbackRequestLog.Message);
                                            
                                            if (requestEntry?.Message?.Params?.Request?.Headers != null)
                                            {
                                                foreach (var header in requestEntry.Message.Params.Request.Headers)
                                                {
                                                    if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        feedbackAuthToken = header.Value;
                                                        break;
                                                    }
                                                }
                                            }
                                        }

                                        // Only proceed if we have a valid auth token
                                        if (!string.IsNullOrEmpty(feedbackAuthToken))
                                        {
                                            // Ensure Artist directory exists
                                            if (!Directory.Exists("Artist"))
                                            {
                                                Directory.CreateDirectory("Artist");
                                            }

                                            // Save the auth token to ArtistAuth.txt
                                            await File.WriteAllTextAsync("Artist/ArtistAuth.txt", feedbackAuthToken);
                                            Log.Information("Auth token saved to Artist/ArtistAuth.txt");
                                        }
                                    }
                                    // Monitor artist station page API endpoint
                                    else if (url.Contains("api.edge-gateway.siriusxm.com/page/v1/page/artist-station"))
                                    {
                                        Log.Information("Artist station page API request detected: {Url}", url);

                                        // Get the auth token from the original request
                                        var artistAuthToken = "";

                                        var artistRequestLog = logs.FirstOrDefault(l =>
                                        {
                                            var entry = JsonSerializer.Deserialize<PerformanceLogEntry>(l.Message);
                                            return entry?.Message?.Method == "Network.requestWillBeSent" &&
                                                   entry?.Message?.Params?.RequestId == logEntry.Message.Params?.RequestId;
                                        });

                                        if (artistRequestLog != null)
                                        {
                                            var requestEntry = JsonSerializer.Deserialize<PerformanceLogEntry>(artistRequestLog.Message);
                                            
                                            if (requestEntry?.Message?.Params?.Request?.Headers != null)
                                            {
                                                foreach (var header in requestEntry.Message.Params.Request.Headers)
                                                {
                                                    if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        artistAuthToken = header.Value;
                                                        break;
                                                    }
                                                }
                                            }
                                        }

                                        // Only proceed if we have a valid auth token
                                        if (!string.IsNullOrEmpty(artistAuthToken))
                                        {
                                            // Ensure Artist directory exists
                                            if (!Directory.Exists("Artist"))
                                            {
                                                Directory.CreateDirectory("Artist");
                                            }

                                            // Save the auth token to ArtistAuth.txt
                                            await File.WriteAllTextAsync("Artist/ArtistAuth.txt", artistAuthToken);
                                            Log.Information("Auth token saved to Artist/ArtistAuth.txt");
                                        }
                                    }
                                    // Monitor tuneSource endpoint
                                    else if (url.Contains("api.edge-gateway.siriusxm.com/playback/play/v1/tuneSource"))
                                    {
                                        Log.Information("TuneSource request detected: {Url}", url);

                                        // Get the auth token and payload from the original request
                                        var authToken = "";
                                        var requestPayload = "";

                                        var requestLog = logs.FirstOrDefault(l =>
                                        {
                                            var entry = JsonSerializer.Deserialize<PerformanceLogEntry>(l.Message);
                                            return entry?.Message?.Method == "Network.requestWillBeSent" &&
                                                   entry?.Message?.Params?.RequestId == logEntry.Message.Params?.RequestId;
                                        });

                                        if (requestLog != null)
                                        {
                                            var requestEntry = JsonSerializer.Deserialize<PerformanceLogEntry>(requestLog.Message);
                                            
                                            if (requestEntry?.Message?.Params?.Request?.Headers != null)
                                            {
                                                foreach (var header in requestEntry.Message.Params.Request.Headers)
                                                {
                                                    if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        authToken = header.Value;
                                                        break;
                                                    }
                                                }
                                            }

                                            // Get the request payload
                                            requestPayload = requestEntry?.Message?.Params?.Request?.PostData;
                                        }

                                        // Store the last tuneSource request data
                                        lastTuneSourceUrl = url;
                                        lastTuneSourcePayload = requestPayload;
                                        lastTuneSourceAuthToken = authToken;

                                        // Only proceed if we have a valid auth token and payload
                                        if (!string.IsNullOrEmpty(authToken) && !string.IsNullOrEmpty(requestPayload))
                                        {
                                            // Ensure Stations directory exists
                                            if (!Directory.Exists("Stations"))
                                            {
                                                Directory.CreateDirectory("Stations");
                                            }

                                            // Ensure tunesource.txt exists in Stations directory
                                            if (!File.Exists("Stations/tunesource.txt"))
                                            {
                                                File.Create("Stations/tunesource.txt").Dispose();
                                            }

                                            // Save the auth token to tunesource.txt (overwrite)
                                            await File.WriteAllTextAsync("Stations/tunesource.txt", authToken);
                                            Log.Information("Auth token saved to Stations/tunesource.txt");

                                            try
                                            {
                                                await SendTuneSourceRequest(url, requestPayload, authToken);
                                            }
                                            catch (Exception ex)
                                            {
                                                Log.Error(ex, "Error making POST request to tuneSource endpoint");
                                            }
                                        }
                                    }
                                    // Monitor Conviva WSG endpoint
                                    else if (url.EndsWith("conviva.com/0/wsg", StringComparison.OrdinalIgnoreCase))
                                    {
                                        Log.Information("Conviva WSG request detected: {Url}", url);

                                        // Get the payload from the original request
                                        var wsgPayload = "";

                                        var wsgRequestLog = logs.FirstOrDefault(l =>
                                        {
                                            var entry = JsonSerializer.Deserialize<PerformanceLogEntry>(l.Message);
                                            return entry?.Message?.Method == "Network.requestWillBeSent" &&
                                                   entry?.Message?.Params?.RequestId == logEntry.Message.Params?.RequestId;
                                        });

                                        if (wsgRequestLog != null)
                                        {
                                            var requestEntry = JsonSerializer.Deserialize<PerformanceLogEntry>(wsgRequestLog.Message);
                                            wsgPayload = requestEntry?.Message?.Params?.Request?.PostData;
                                        }

                                        // Only proceed if we have a valid payload
                                        if (!string.IsNullOrEmpty(wsgPayload))
                                        {
                                            try
                                            {
                                                // Parse the payload to check if it's a valid JSON
                                                var jsonElement = JsonSerializer.Deserialize<JsonElement>(wsgPayload);
                                                
                                                // Check if the payload contains the required fwv value
                                                bool hasRequiredFwv = false;
                                                
                                                // Check in pm object
                                                if (jsonElement.TryGetProperty("pm", out var pmElement) && 
                                                    pmElement.TryGetProperty("fwv", out var pmFwvElement) &&
                                                    pmFwvElement.GetString() == "howler - 2.2.4")
                                                {
                                                    hasRequiredFwv = true;
                                                }
                                                
                                                // Check in root object
                                                if (!hasRequiredFwv && 
                                                    jsonElement.TryGetProperty("fwv", out var rootFwvElement) &&
                                                    rootFwvElement.GetString() == "howler - 2.2.4")
                                                {
                                                    hasRequiredFwv = true;
                                                }

                                                // Check if payload contains new/old objects in evs array
                                                bool hasNewOldObjects = false;
                                                if (jsonElement.TryGetProperty("evs", out var evsElement) && 
                                                    evsElement.ValueKind == JsonValueKind.Array)
                                                {
                                                    foreach (var ev in evsElement.EnumerateArray())
                                                    {
                                                        if (ev.TryGetProperty("new", out _) || ev.TryGetProperty("old", out _))
                                                        {
                                                            hasNewOldObjects = true;
                                                            break;
                                                        }
                                                    }
                                                }

                                                // Only save if the required fwv value is found and no new/old objects exist
                                                if (hasRequiredFwv && !hasNewOldObjects)
                                                {
                                                    // Ensure Stations directory exists
                                                    if (!Directory.Exists("Stations"))
                                                    {
                                                        Directory.CreateDirectory("Stations");
                                                    }

                                                    var formattedJson = JsonSerializer.Serialize(jsonElement, new JsonSerializerOptions 
                                                    { 
                                                        WriteIndented = true 
                                                    });

                                                    // Save the payload to Now.json
                                                    await File.WriteAllTextAsync("Stations/Now.json", formattedJson);
                                                    Log.Information("WSG payload with required fwv saved to Stations/Now.json");

                                                    // Extract track information
                                                    string artistName = "";
                                                    string trackName = "";
                                                    string streamUrl = "";

                                                    // Get artist name - check both root level and tags
                                                    if (jsonElement.TryGetProperty("artistName", out var artistElement))
                                                    {
                                                        artistName = artistElement.GetString() ?? "";
                                                    }
                                                    else if (jsonElement.TryGetProperty("tags", out var tagsElement) && 
                                                             tagsElement.TryGetProperty("artistName", out var tagsArtistElement))
                                                    {
                                                        artistName = tagsArtistElement.GetString() ?? "";
                                                    }

                                                    // Get track name (an)
                                                    if (jsonElement.TryGetProperty("an", out var trackElement))
                                                    {
                                                        trackName = trackElement.GetString() ?? "";
                                                    }

                                                    // Get stream URL
                                                    if (jsonElement.TryGetProperty("url", out var urlElement))
                                                    {
                                                        streamUrl = urlElement.GetString() ?? "";
                                                    }

                                                    // Only proceed if we have a valid URL
                                                    if (!string.IsNullOrEmpty(streamUrl))
                                                    {
                                                        await Dispatcher.InvokeAsync(() =>
                                                        {
                                                            // Check if this URL already exists in the stream entries
                                                            var existingEntry = streamEntries.FirstOrDefault(e => e.Url == streamUrl);
                                                            
                                                            if (existingEntry != null)
                                                            {
                                                                // If the existing entry is unnamed, update it with the new information
                                                                if (existingEntry.TrackName == "Unnamed MP4")
                                                                {
                                                                    existingEntry.TrackName = trackName;
                                                                    existingEntry.ArtistName = artistName;
                                                                    Log.Information("Updated unnamed stream entry with track info: {Track} - {Artist}", trackName, artistName);
                                                                }
                                                            }
                                                            else
                                                            {
                                                                // Check if this track/artist combination already exists
                                                                var duplicateEntry = streamEntries.FirstOrDefault(e => 
                                                                    e.TrackName == trackName && 
                                                                    e.ArtistName == artistName && 
                                                                    !string.IsNullOrEmpty(trackName) && 
                                                                    !string.IsNullOrEmpty(artistName));

                                                                if (duplicateEntry == null)
                                                                {
                                                                    // Add new entry only if it's not a duplicate
                                                                    streamEntries.Add(new StreamEntry
                                                                    {
                                                                        Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                                                                        StreamType = "mp4",
                                                                        Url = streamUrl,
                                                                        TrackName = trackName,
                                                                        ArtistName = artistName,
                                                                        PreferredImageUrl = null
                                                                    });
                                                                    Log.Information("Added new stream entry: {Track} - {Artist}", trackName, artistName);
                                                                }
                                                                else
                                                                {
                                                                    Log.Debug("Skipping duplicate stream entry: {Track} - {Artist}", trackName, artistName);
                                                                }
                                                            }
                                                            UpdateTotalCapturedCount();
                                                        });
                                                    }
                                                }
                                                else
                                                {
                                                    if (!hasRequiredFwv)
                                                    {
                                                        Log.Debug("Skipping WSG payload - does not contain required fwv value");
                                                    }
                                                    else if (hasNewOldObjects)
                                                    {
                                                        Log.Debug("Skipping WSG payload - contains new/old objects in evs array");
                                                    }
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                Log.Error(ex, "Error processing WSG payload");
                                            }
                                        }
                                    }
                                    // Monitor peek endpoint
                                    else if (url.Contains("api.edge-gateway.siriusxm.com/playback/play/v1/peek"))
                                    {
                                        Log.Information("Peek request detected: {Url}", url);

                                        // Get the auth token and payload from the original request
                                        var peekAuthToken = "";
                                        var peekRequestPayload = "";

                                        var peekRequestLog = logs.FirstOrDefault(l =>
                                        {
                                            var entry = JsonSerializer.Deserialize<PerformanceLogEntry>(l.Message);
                                            return entry?.Message?.Method == "Network.requestWillBeSent" &&
                                                   entry?.Message?.Params?.RequestId == logEntry.Message.Params?.RequestId;
                                        });

                                        if (peekRequestLog != null)
                                        {
                                            var requestEntry = JsonSerializer.Deserialize<PerformanceLogEntry>(peekRequestLog.Message);
                                            
                                            if (requestEntry?.Message?.Params?.Request?.Headers != null)
                                            {
                                                foreach (var header in requestEntry.Message.Params.Request.Headers)
                                                {
                                                    if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        peekAuthToken = header.Value;
                                                        break;
                                                    }
                                                }
                                            }

                                            // Get the request payload
                                            peekRequestPayload = requestEntry?.Message?.Params?.Request?.PostData;
                                        }

                                        // Only proceed if we have a valid auth token and payload
                                        if (!string.IsNullOrEmpty(peekAuthToken) && !string.IsNullOrEmpty(peekRequestPayload))
                                        {
                                            // Ensure Stations directory exists
                                            if (!Directory.Exists("Stations"))
                                            {
                                                Directory.CreateDirectory("Stations");
                                            }

                                            // Save the auth token to peek.txt (overwrite)
                                            await File.WriteAllTextAsync("Stations/peek.txt", peekAuthToken);
                                            Log.Information("Auth token saved to Stations/peek.txt");

                                            // Save the payload to peek_payload.json
                                            try
                                            {
                                                // Format the JSON payload with proper indentation
                                                var jsonElement = JsonSerializer.Deserialize<JsonElement>(peekRequestPayload);
                                                var formattedJson = JsonSerializer.Serialize(jsonElement, new JsonSerializerOptions 
                                                { 
                                                    WriteIndented = true 
                                                });
                                                await File.WriteAllTextAsync("Stations/peek_payload.json", formattedJson);
                                                Log.Information("Peek payload saved to Stations/peek_payload.json");

                                                // Replay the request
                                                using (var httpClient = new HttpClient())
                                                {
                                                    httpClient.DefaultRequestHeaders.Add("Authorization", peekAuthToken);
                                                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                                                    
                                                    var content = new StringContent(
                                                        peekRequestPayload,
                                                        System.Text.Encoding.UTF8,
                                                        "application/json"
                                                    );
                                                    
                                                    var response = await httpClient.PostAsync(url, content);
                                                    var peekResponseBody = await response.Content.ReadAsStringAsync();

                                                    if (response.IsSuccessStatusCode)
                                                    {
                                                        // Format the JSON response with proper indentation
                                                        var responseElement = JsonSerializer.Deserialize<JsonElement>(peekResponseBody);
                                                        var formattedResponse = JsonSerializer.Serialize(responseElement, new JsonSerializerOptions 
                                                        { 
                                                            WriteIndented = true 
                                                        });

                                                        // Save the formatted JSON to peek_response.json
                                                        await File.WriteAllTextAsync("Stations/peek_response.json", formattedResponse);
                                                        Log.Information("Peek response saved to Stations/peek_response.json");

                                                        // Process stream information from peek response
                                                        if (responseElement.TryGetProperty("streams", out var streamsElement) && streamsElement.ValueKind == JsonValueKind.Array)
                                                        {
                                                            foreach (var stream in streamsElement.EnumerateArray())
                                                            {
                                                                if (stream.TryGetProperty("metadata", out var metadata) &&
                                                                    metadata.TryGetProperty("artist", out var artist) &&
                                                                    artist.TryGetProperty("items", out var items) &&
                                                                    items.ValueKind == JsonValueKind.Array)
                                                                {
                                                                    foreach (var item in items.EnumerateArray())
                                                                    {
                                                                        if (item.TryGetProperty("name", out var nameElement) &&
                                                                            item.TryGetProperty("artistName", out var artistNameElement))
                                                                        {
                                                                            var trackName = nameElement.GetString();
                                                                            var artistName = artistNameElement.GetString();

                                                                            if (stream.TryGetProperty("urls", out var urls) &&
                                                                                urls.ValueKind == JsonValueKind.Array)
                                                                            {
                                                                                foreach (var urlEntry in urls.EnumerateArray())
                                                                                {
                                                                                    if (urlEntry.TryGetProperty("url", out var urlElement) &&
                                                                                        urlEntry.TryGetProperty("isPrimary", out var isPrimaryElement) &&
                                                                                        isPrimaryElement.GetBoolean())
                                                                                    {
                                                                                        var streamUrl = urlElement.GetString();
                                                                                        StreamEntry existingEntry;
                                                                                        // Check if this is a duplicate file
                                                                                        if (IsDuplicateStream(streamUrl, out existingEntry))
                                                                                        {
                                                                                            // If the existing entry is unnamed, update it with the title information
                                                                                            if (existingEntry.TrackName == "Unnamed MP4")
                                                                                            {
                                                                                                await Dispatcher.InvokeAsync(() =>
                                                                                                {
                                                                                                    existingEntry.TrackName = trackName;
                                                                                                    existingEntry.ArtistName = artistName;
                                                                                                    // Refresh the ListView to show the updated information
                                                                                                    StreamListView.Items.Refresh();
                                                                                                });
                                                                                                Log.Information("Updated unnamed entry with title: {TrackName} - {ArtistName}", trackName, artistName);
                                                                                            }
                                                                                            Log.Debug("Skipping duplicate stream URL: {Url}", streamUrl);
                                                                                            continue;
                                                                                        }
                                                                                        await Dispatcher.InvokeAsync(() =>
                                                                                        {
                                                                                            string preferredImageUrl = null;
                                                                                            if (stream.TryGetProperty("metadata", out var metadata) &&
                                                                                                metadata.TryGetProperty("artist", out var artist) &&
                                                                                                artist.TryGetProperty("items", out var items) &&
                                                                                                items.ValueKind == JsonValueKind.Array &&
                                                                                                items.GetArrayLength() > 0)
                                                                                            {
                                                                                                var firstItem = items[0];
                                                                                                if (firstItem.TryGetProperty("images", out var images) &&
                                                                                                    images.TryGetProperty("tile", out var tile) &&
                                                                                                    tile.TryGetProperty("aspect_1x1", out var aspect) &&
                                                                                                    aspect.TryGetProperty("preferredImage", out var preferredImage) &&
                                                                                                    preferredImage.TryGetProperty("url", out var imageUrl))
                                                                                                {
                                                                                                    preferredImageUrl = StreamEntry.DecodeImageUrl(imageUrl.GetString());
                                                                                                }
                                                                                            }

                                                                                            streamEntries.Add(new StreamEntry
                                                                                            {
                                                                                                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                                                                                                StreamType = "mp4",
                                                                                                Url = streamUrl,
                                                                                                TrackName = trackName,
                                                                                                ArtistName = artistName,
                                                                                                PreferredImageUrl = preferredImageUrl
                                                                                            });
                                                                                            UpdateTotalCapturedCount();
                                                                                        });
                                                                                        break;
                                                                                    }
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        Log.Warning("Failed to get response from peek endpoint. Status code: {StatusCode}", response.StatusCode);
                                                    }
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                Log.Error(ex, "Error processing peek request");
                                            }
                                        }
                                    }
                                    // Monitor playback key endpoint
                                    else if (url.Contains("api.edge-gateway.siriusxm.com/playback/key/v1/"))
                                    {
                                        Log.Information("Playback key request detected: {Url}", url);

                                        var keyAuthToken = "";

                                        var keyRequestLog = logs.FirstOrDefault(l =>
                                        {
                                            var entry = JsonSerializer.Deserialize<PerformanceLogEntry>(l.Message);
                                            return entry?.Message?.Method == "Network.requestWillBeSent" &&
                                                   entry?.Message?.Params?.RequestId == logEntry.Message.Params?.RequestId;
                                        });

                                        if (keyRequestLog != null)
                                        {
                                            var requestEntry = JsonSerializer.Deserialize<PerformanceLogEntry>(keyRequestLog.Message);

                                            if (requestEntry?.Message?.Params?.Request?.Headers != null)
                                            {
                                                foreach (var header in requestEntry.Message.Params.Request.Headers)
                                                {
                                                    if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        keyAuthToken = header.Value;
                                                        break;
                                                    }
                                                }
                                            }
                                        }

                                        if (!string.IsNullOrEmpty(keyAuthToken))
                                        {
                                            try
                                            {
                                                using var httpClient = new HttpClient();
                                                httpClient.DefaultRequestHeaders.Add("Authorization", keyAuthToken);
                                                httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

                                                var response = await httpClient.GetAsync(url);
                                                var body = await response.Content.ReadAsStringAsync();

                                                if (response.IsSuccessStatusCode)
                                                {
                                                    // Ensure directory exists
                                                    if (!Directory.Exists("HLSKey"))
                                                    {
                                                        Directory.CreateDirectory("HLSKey");
                                                    }

                                                    // Save bearer token for reference
                                                    await File.WriteAllTextAsync("HLSKey/authorization Bearer.txt", keyAuthToken);

                                                    // Save prettified JSON
                                                    var jsonElement = JsonSerializer.Deserialize<JsonElement>(body);
                                                    var formatted = JsonSerializer.Serialize(jsonElement, new JsonSerializerOptions { WriteIndented = true });
                                                    await File.WriteAllTextAsync("HLSKey/response.json", formatted);
                                                    Log.Information("Playback key saved to HLSKey/response.json");
                                                }
                                                else
                                                {
                                                    Log.Warning("Playback key request failed: {StatusCode}", response.StatusCode);
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                Log.Error(ex, "Error fetching playback key response");
                                            }
                                        }
                                        else
                                        {
                                            Log.Warning("Missing auth token for playback key request; skipping fetch.");
                                        }
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
        if (sender is Button button && button.DataContext is StreamEntry item)
        {
            var downloadWindow = new DownloadWindow(
                item.Url,
                item.TrackName,
                item.ArtistName,
                item.PreferredImageUrl);
            downloadWindow.Owner = this;
            downloadWindow.ShowDialog();
        }
    }


    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        stationFeedbackWatcher?.Dispose();
        StopMonitoring().Wait();
        Log.CloseAndFlush();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        try
        {
            // Scale based on window size relative to base design
            double widthRatio = ActualWidth / BaseWidth;
            double heightRatio = ActualHeight / BaseHeight;
            double scale = Math.Min(MaxScale, Math.Max(MinScale, Math.Min(widthRatio, heightRatio)));

            RootScale.ScaleX = scale;
            RootScale.ScaleY = scale;

            // Collapse artist section on narrow widths
            bool collapseRight = ActualWidth < 1050;
            if (collapseRight)
            {
                RightColumn.Width = new GridLength(0);
                LeftColumn.Width = new GridLength(1, GridUnitType.Star);
                ArtistSectionBorder.Visibility = Visibility.Collapsed;
            }
            else
            {
                RightColumn.Width = new GridLength(1, GridUnitType.Star);
                LeftColumn.Width = new GridLength(1, GridUnitType.Star);
                ArtistSectionBorder.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error applying responsive layout");
        }
    }

    private async Task ProcessArtistStationUrl(string url)
    {
        await artistStations?.ProcessArtistStationUrl(url);
    }

    private async void PlayArtistStation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is Artist.ArtistEntry entry)
        {
            await artistStations?.PlayArtistStation(entry, status => StatusText.Text = status);
        }
    }

    private void ClearStreams_Click(object sender, RoutedEventArgs e)
    {
        streamEntries.Clear();
        UpdateTotalCapturedCount();
        StatusText.Text = "Stream activity cleared";
        Log.Information("Stream activity cleared");
    }

    private void UpdateTotalCapturedCount()
    {
        Dispatcher.Invoke(() =>
        {
            TotalCapturedCount.Text = streamEntries.Count.ToString();
        });
    }

    private void SetupStationFeedbackWatcher()
    {
        try
        {
            stationFeedbackWatcher = new FileSystemWatcher("Stations");
            stationFeedbackWatcher.Filter = "station_feedback.json";
            stationFeedbackWatcher.NotifyFilter = NotifyFilters.LastWrite;
            stationFeedbackWatcher.Changed += OnStationFeedbackChanged;
            stationFeedbackWatcher.EnableRaisingEvents = true;
            Log.Information("Station feedback file watcher initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error setting up station feedback watcher");
        }
    }

    private async void OnStationFeedbackChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            // Wait a short moment to ensure the file is completely written
            await Task.Delay(100);

            if (!string.IsNullOrEmpty(lastTuneSourceUrl) && 
                !string.IsNullOrEmpty(lastTuneSourcePayload) && 
                !string.IsNullOrEmpty(lastTuneSourceAuthToken))
            {
                Log.Information("Station feedback updated, sending tuneSource request");
                await SendTuneSourceRequest(lastTuneSourceUrl, lastTuneSourcePayload, lastTuneSourceAuthToken);
            }
            else
            {
                Log.Warning("Cannot send tuneSource request - missing previous request data");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling station feedback change");
        }
    }

    private async Task SendTuneSourceRequest(string url, string payload, string authToken)
    {
        try
        {
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", authToken);
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                
                var content = new StringContent(
                    payload,
                    System.Text.Encoding.UTF8,
                    "application/json"
                );
                
                var response = await httpClient.PostAsync(url, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var jsonElement = JsonSerializer.Deserialize<JsonElement>(responseBody);
                    var formattedJson = JsonSerializer.Serialize(jsonElement, new JsonSerializerOptions 
                    { 
                        WriteIndented = true 
                    });

                    await File.WriteAllTextAsync("Stations/Playlist.json", formattedJson);
                    Log.Information("Response saved to Stations/Playlist.json");

                    // Check for podcast episodes and channel-linear in the response
                    if (jsonElement.TryGetProperty("type", out var typeElement))
                    {
                        var type = typeElement.GetString();
                        if (type == "episode-podcast")
                        {
                            Log.Information("Podcast episode detected in playlist: {Id}", 
                                jsonElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : "unknown");

                            // Process podcast streams
                            if (jsonElement.TryGetProperty("streams", out var podcastStreamsElement) && 
                                podcastStreamsElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var stream in podcastStreamsElement.EnumerateArray())
                                {
                                    if (stream.TryGetProperty("urls", out var urls) && 
                                        urls.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var urlEntry in urls.EnumerateArray())
                                        {
                                            if (urlEntry.TryGetProperty("isPrimary", out var isPrimaryElement) && 
                                                isPrimaryElement.GetBoolean() &&
                                                urlEntry.TryGetProperty("url", out var urlElement))
                                            {
                                                string streamUrl = urlElement.GetString();
                                                string episodeName = "Unknown Episode";
                                                string showName = "Unknown Show";
                                                string preferredImageUrl = null;

                                                // Get metadata
                                                if (stream.TryGetProperty("metadata", out var metadata) &&
                                                    metadata.TryGetProperty("podcast", out var podcast) &&
                                                    podcast.TryGetProperty("episode", out var episode))
                                                {
                                                    if (episode.TryGetProperty("name", out var nameElement))
                                                    {
                                                        episodeName = nameElement.GetString();
                                                    }
                                                    if (episode.TryGetProperty("showName", out var showNameElement))
                                                    {
                                                        showName = showNameElement.GetString();
                                                    }
                                                    if (episode.TryGetProperty("showImages", out var showImages) &&
                                                        showImages.TryGetProperty("tile", out var tile) &&
                                                        tile.TryGetProperty("aspect_1x1", out var aspect) &&
                                                        aspect.TryGetProperty("preferredImage", out var preferredImage) &&
                                                        preferredImage.TryGetProperty("url", out var imageUrl))
                                                    {
                                                        preferredImageUrl = StreamEntry.DecodeImageUrl(imageUrl.GetString());
                                                    }
                                                }

                                                await Dispatcher.InvokeAsync(() =>
                                                {
                                                    streamEntries.Add(new StreamEntry
                                                    {
                                                        Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                                                        StreamType = "podcast",
                                                        Url = streamUrl,
                                                        TrackName = episodeName,
                                                        ArtistName = showName,
                                                        PreferredImageUrl = preferredImageUrl
                                                    });
                                                    UpdateTotalCapturedCount();
                                                });
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else if (type == "episode-video")
                        {
                            Log.Information("Video episode detected in playlist: {Id}", 
                                jsonElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : "unknown");

                            // Process video episode streams (M3U8)
                            if (jsonElement.TryGetProperty("streams", out var videoStreamsElement) && 
                                videoStreamsElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var stream in videoStreamsElement.EnumerateArray())
                                {
                                    if (stream.TryGetProperty("urls", out var urls) && 
                                        urls.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var urlEntry in urls.EnumerateArray())
                                        {
                                            if (urlEntry.TryGetProperty("name", out var nameElement) &&
                                                nameElement.GetString() == "primary" &&
                                                urlEntry.TryGetProperty("url", out var urlElement))
                                            {
                                                string streamUrl = urlElement.GetString();
                                                string episodeName = "Unknown Episode";
                                                string channelName = "Unknown Channel";
                                                string preferredImageUrl = null;

                                                // Get metadata from metadata.vod.episode
                                                if (jsonElement.TryGetProperty("metadata", out var metadata) &&
                                                    metadata.TryGetProperty("vod", out var vod) &&
                                                    vod.TryGetProperty("episode", out var episode))
                                                {
                                                    if (episode.TryGetProperty("name", out var episodeNameElement))
                                                    {
                                                        episodeName = episodeNameElement.GetString();
                                                    }
                                                    if (vod.TryGetProperty("channelName", out var channelNameElement))
                                                    {
                                                        channelName = channelNameElement.GetString();
                                                    }
                                                    if (episode.TryGetProperty("images", out var images) &&
                                                        images.TryGetProperty("tile", out var tile) &&
                                                        tile.TryGetProperty("aspect_1x1", out var aspect) &&
                                                        aspect.TryGetProperty("preferredImage", out var preferredImage) &&
                                                        preferredImage.TryGetProperty("url", out var imageUrl))
                                                    {
                                                        preferredImageUrl = StreamEntry.DecodeImageUrl(imageUrl.GetString());
                                                    }
                                                }

                                                // Check if this URL already exists in the stream entries
                                                StreamEntry existingEntry;
                                                if (IsDuplicateStream(streamUrl, out existingEntry))
                                                {
                                                    // If the existing entry is unnamed, update it with the metadata
                                                    if (existingEntry.TrackName == "Unnamed M3U8")
                                                    {
                                                        await Dispatcher.InvokeAsync(() =>
                                                        {
                                                            existingEntry.TrackName = episodeName;
                                                            existingEntry.ArtistName = channelName;
                                                            existingEntry.PreferredImageUrl = preferredImageUrl;
                                                            // Refresh the ListView to show the updated information
                                                            StreamListView.Items.Refresh();
                                                        });
                                                        Log.Information("Updated unnamed M3U8 entry with metadata: {TrackName} - {ArtistName}", episodeName, channelName);
                                                    }
                                                    Log.Debug("Skipping duplicate M3U8 stream URL: {Url}", streamUrl);
                                                    continue;
                                                }

                                                await Dispatcher.InvokeAsync(() =>
                                                {
                                                    streamEntries.Add(new StreamEntry
                                                    {
                                                        Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                                                        StreamType = "m3u8",
                                                        Url = streamUrl,
                                                        TrackName = episodeName,
                                                        ArtistName = channelName,
                                                        PreferredImageUrl = preferredImageUrl
                                                    });
                                                    UpdateTotalCapturedCount();
                                                });
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else if (type == "episode-audio")
                        {
                            Log.Information("Audio episode detected in playlist: {Id}", 
                                jsonElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : "unknown");

                            // Process audio episode streams (M3U8)
                            if (jsonElement.TryGetProperty("streams", out var audioStreamsElement) && 
                                audioStreamsElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var stream in audioStreamsElement.EnumerateArray())
                                {
                                    if (stream.TryGetProperty("urls", out var urls) && 
                                        urls.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var urlEntry in urls.EnumerateArray())
                                        {
                                            if (urlEntry.TryGetProperty("name", out var nameElement) &&
                                                nameElement.GetString() == "primary" &&
                                                urlEntry.TryGetProperty("url", out var urlElement))
                                            {
                                                string streamUrl = urlElement.GetString();
                                                string episodeName = "Unknown Episode";
                                                string channelName = "Unknown Channel";
                                                string preferredImageUrl = null;

                                                // Get metadata from metadata.aod.episode
                                                if (stream.TryGetProperty("metadata", out var metadata) &&
                                                    metadata.TryGetProperty("aod", out var aod) &&
                                                    aod.TryGetProperty("episode", out var episode))
                                                {
                                                    if (episode.TryGetProperty("name", out var episodeNameElement))
                                                    {
                                                        episodeName = episodeNameElement.GetString();
                                                    }
                                                    if (aod.TryGetProperty("channelName", out var channelNameElement))
                                                    {
                                                        channelName = channelNameElement.GetString();
                                                    }
                                                    if (episode.TryGetProperty("images", out var images) &&
                                                        images.TryGetProperty("tile", out var tile) &&
                                                        tile.TryGetProperty("aspect_1x1", out var aspect) &&
                                                        aspect.TryGetProperty("preferredImage", out var preferredImage) &&
                                                        preferredImage.TryGetProperty("url", out var imageUrl))
                                                    {
                                                        preferredImageUrl = StreamEntry.DecodeImageUrl(imageUrl.GetString());
                                                    }
                                                }

                                                // Check if this URL already exists in the stream entries
                                                StreamEntry existingEntry;
                                                if (IsDuplicateStream(streamUrl, out existingEntry))
                                                {
                                                    // If the existing entry is unnamed, update it with the metadata
                                                    if (existingEntry.TrackName == "Unnamed M3U8")
                                                    {
                                                        await Dispatcher.InvokeAsync(() =>
                                                        {
                                                            existingEntry.TrackName = episodeName;
                                                            existingEntry.ArtistName = channelName;
                                                            existingEntry.PreferredImageUrl = preferredImageUrl;
                                                            // Refresh the ListView to show the updated information
                                                            StreamListView.Items.Refresh();
                                                        });
                                                        Log.Information("Updated unnamed M3U8 entry with metadata: {TrackName} - {ArtistName}", episodeName, channelName);
                                                    }
                                                    Log.Debug("Skipping duplicate M3U8 stream URL: {Url}", streamUrl);
                                                    continue;
                                                }

                                                await Dispatcher.InvokeAsync(() =>
                                                {
                                                    streamEntries.Add(new StreamEntry
                                                    {
                                                        Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                                                        StreamType = "m3u8",
                                                        Url = streamUrl,
                                                        TrackName = episodeName,
                                                        ArtistName = channelName,
                                                        PreferredImageUrl = preferredImageUrl
                                                    });
                                                    UpdateTotalCapturedCount();
                                                });
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else if (type == "channel-linear")
                        {
                            Log.Information("Channel Linear detected in playlist: {Id}", 
                                jsonElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : "unknown");
                        }
                    }

                    // Process stream information
                    if (jsonElement.TryGetProperty("streams", out var streamsElement) && streamsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var stream in streamsElement.EnumerateArray())
                        {
                            if (stream.TryGetProperty("metadata", out var metadata))
                            {
                                string trackName = "Unknown";
                                string artistName = "Unknown";
                                string preferredImageUrl = null;

                                // Primary: artist items (music)
                                if (metadata.TryGetProperty("artist", out var artist) &&
                                    artist.TryGetProperty("items", out var items) &&
                                    items.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var item in items.EnumerateArray())
                                    {
                                        if (item.TryGetProperty("name", out var nameElement) &&
                                            item.TryGetProperty("artistName", out var artistNameElement))
                                        {
                                            trackName = nameElement.GetString() ?? trackName;
                                            artistName = artistNameElement.GetString() ?? artistName;

                                            if (item.TryGetProperty("images", out var images) &&
                                                images.TryGetProperty("tile", out var tile) &&
                                                tile.TryGetProperty("aspect_1x1", out var aspect) &&
                                                aspect.TryGetProperty("preferredImage", out var preferredImage) &&
                                                preferredImage.TryGetProperty("url", out var imageUrl))
                                            {
                                                preferredImageUrl = StreamEntry.DecodeImageUrl(imageUrl.GetString());
                                            }
                                            break;
                                        }
                                    }
                                }
                                // Fallback: aod episodes (talk/podcast linear)
                                else if (metadata.TryGetProperty("aod", out var aod) &&
                                         aod.TryGetProperty("episode", out var episode))
                                {
                                    if (episode.TryGetProperty("name", out var epName))
                                    {
                                        trackName = epName.GetString() ?? trackName;
                                    }

                                    if (aod.TryGetProperty("channelName", out var channelNameProp))
                                    {
                                        artistName = channelNameProp.GetString() ?? artistName;
                                    }
                                    else if (episode.TryGetProperty("showName", out var showNameProp))
                                    {
                                        artistName = showNameProp.GetString() ?? artistName;
                                    }

                                    // images in episode.images
                                    if (episode.TryGetProperty("images", out var epImages) &&
                                        epImages.TryGetProperty("tile", out var tile) &&
                                        tile.TryGetProperty("aspect_1x1", out var aspect) &&
                                        aspect.TryGetProperty("preferredImage", out var preferredImage) &&
                                        preferredImage.TryGetProperty("url", out var imageUrl))
                                    {
                                        preferredImageUrl = StreamEntry.DecodeImageUrl(imageUrl.GetString());
                                    }
                                    // fallback: showImages
                                    else if (episode.TryGetProperty("showImages", out var showImages) &&
                                             showImages.TryGetProperty("tile", out var sTile) &&
                                             sTile.TryGetProperty("aspect_1x1", out var sAspect) &&
                                             sAspect.TryGetProperty("preferredImage", out var sPreferred) &&
                                             sPreferred.TryGetProperty("url", out var sUrl))
                                    {
                                        preferredImageUrl = StreamEntry.DecodeImageUrl(sUrl.GetString());
                                    }
                                }

                                if (stream.TryGetProperty("urls", out var urls) &&
                                    urls.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var urlEntry in urls.EnumerateArray())
                                    {
                                        if (urlEntry.TryGetProperty("url", out var urlElement) &&
                                            urlEntry.TryGetProperty("isPrimary", out var isPrimaryElement) &&
                                            isPrimaryElement.GetBoolean())
                                        {
                                            var streamUrl = urlElement.GetString();
                                            StreamEntry existingEntry;
                                            // Check if this is a duplicate file
                                            if (IsDuplicateStream(streamUrl, out existingEntry))
                                            {
                                                // If the existing entry is unnamed, update it with the title information
                                                if (existingEntry.TrackName == "Unnamed MP4")
                                                {
                                                    await Dispatcher.InvokeAsync(() =>
                                                    {
                                                        existingEntry.TrackName = trackName;
                                                        existingEntry.ArtistName = artistName;
                                                        existingEntry.PreferredImageUrl = preferredImageUrl ?? existingEntry.PreferredImageUrl;
                                                        StreamListView.Items.Refresh();
                                                    });
                                                    Log.Information("Updated unnamed entry with title: {TrackName} - {ArtistName}", trackName, artistName);
                                                }
                                                Log.Debug("Skipping duplicate stream URL: {Url}", streamUrl);
                                                continue;
                                            }
                                            await Dispatcher.InvokeAsync(() =>
                                            {
                                                streamEntries.Add(new StreamEntry
                                                {
                                                    Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                                                    StreamType = "mp4",
                                                    Url = streamUrl,
                                                    TrackName = trackName,
                                                    ArtistName = artistName,
                                                    PreferredImageUrl = preferredImageUrl
                                                });
                                                UpdateTotalCapturedCount();
                                            });
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    Log.Warning("Failed to get response from tuneSource endpoint. Status code: {StatusCode}", response.StatusCode);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error making POST request to tuneSource endpoint");
        }
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (driver == null || !IsMonitoring) return;

        try
        {
            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(5));
            
            if (!_isPaused)
            {
                // Find and click pause button
                var pauseButton = wait.Until(d => d.FindElement(By.CssSelector("button[aria-label='Pause']")));
                pauseButton.Click();
                StatusText.Text = "Playback paused";
                
                // Update button to show play icon
                if (sender is Button button)
                {
                    var pauseIcon = button.FindName("PauseIcon") as System.Windows.Shapes.Path;
                    var playIcon = button.FindName("PlayIcon") as System.Windows.Shapes.Path;
                    if (pauseIcon != null && playIcon != null)
                    {
                        pauseIcon.Visibility = Visibility.Collapsed;
                        playIcon.Visibility = Visibility.Visible;
                        playIcon.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A1A1AA")); // TextSecondary color
                    }
                }
            }
            else
            {
                // Find and click play button
                var playButton = wait.Until(d => d.FindElement(By.CssSelector("button[aria-label='Play']")));
                playButton.Click();
                StatusText.Text = "Playback resumed";
                
                // Update button to show pause icon
                if (sender is Button button)
                {
                    var pauseIcon = button.FindName("PauseIcon") as System.Windows.Shapes.Path;
                    var playIcon = button.FindName("PlayIcon") as System.Windows.Shapes.Path;
                    if (pauseIcon != null && playIcon != null)
                    {
                        pauseIcon.Visibility = Visibility.Visible;
                        playIcon.Visibility = Visibility.Collapsed;
                        pauseIcon.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A1A1AA")); // TextSecondary color
                    }
                }
            }
            
            _isPaused = !_isPaused;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error toggling playback state");
            StatusText.Text = "Error toggling playback";
        }
    }

    private async void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (driver == null || !IsMonitoring) return;

        try
        {
            var wait = new OpenQA.Selenium.Support.UI.WebDriverWait(driver, TimeSpan.FromSeconds(5));
            
            // Try to find the forward button using the SVG path
            try
            {
                var forwardButton = wait.Until(d => d.FindElement(By.CssSelector("svg[viewBox='0 0 24 24'] path[d*='M16.757 4.626']")));
                // Find the parent button element
                var buttonElement = forwardButton.FindElement(By.XPath("./ancestor::button"));
                ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView(true);", buttonElement);
                buttonElement.Click();
                StatusText.Text = "Skipped to next track";
                Log.Information("Clicked forward button");
            }
            catch (OpenQA.Selenium.WebDriverTimeoutException)
            {
                Log.Warning("Could not find forward button");
                StatusText.Text = "Could not find forward button";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error clicking forward button");
            StatusText.Text = "Error skipping track";
        }
    }

    private void PauseButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
        {
            var pauseIcon = button.FindName("PauseIcon") as System.Windows.Shapes.Path;
            var playIcon = button.FindName("PlayIcon") as System.Windows.Shapes.Path;
            if (pauseIcon != null && playIcon != null)
            {
                var whiteBrush = new SolidColorBrush(Colors.White);
                if (pauseIcon.Visibility == Visibility.Visible)
                {
                    pauseIcon.Fill = whiteBrush;
                }
                else
                {
                    playIcon.Fill = whiteBrush;
                }
            }
        }
    }

    private void PauseButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
        {
            var pauseIcon = button.FindName("PauseIcon") as System.Windows.Shapes.Path;
            var playIcon = button.FindName("PlayIcon") as System.Windows.Shapes.Path;
            if (pauseIcon != null && playIcon != null)
            {
                var secondaryBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A1A1AA"));
                if (pauseIcon.Visibility == Visibility.Visible)
                {
                    pauseIcon.Fill = secondaryBrush;
                }
                else
                {
                    playIcon.Fill = secondaryBrush;
                }
            }
        }
    }

    private void ForwardButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
        {
            var forwardIcon = button.FindName("ForwardIcon") as System.Windows.Shapes.Path;
            if (forwardIcon != null)
            {
                forwardIcon.Fill = new SolidColorBrush(Colors.White);
            }
        }
    }

    private void ForwardButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
        {
            var forwardIcon = button.FindName("ForwardIcon") as System.Windows.Shapes.Path;
            if (forwardIcon != null)
            {
                forwardIcon.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A1A1AA"));
            }
        }
    }

    private void SkipBackButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
        {
            var skipBackIcon = button.FindName("SkipBackIcon") as System.Windows.Shapes.Path;
            if (skipBackIcon != null)
            {
                skipBackIcon.Fill = new SolidColorBrush(Colors.White);
            }
        }
    }

    private void SkipBackButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
        {
            var skipBackIcon = button.FindName("SkipBackIcon") as System.Windows.Shapes.Path;
            if (skipBackIcon != null)
            {
                skipBackIcon.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A1A1AA"));
            }
        }
    }

    private async void SkipBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (driver == null || !IsMonitoring) return;

        try
        {
            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(5));
            
            // Try to find the skip back button using the SVG path
            try
            {
                var skipBackButton = wait.Until(d => d.FindElement(By.CssSelector("svg[viewBox='0 0 24 24'] path[d*='M7.764 4.554']")));
                // Find the parent button element
                var buttonElement = skipBackButton.FindElement(By.XPath("./ancestor::button"));
                ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView(true);", buttonElement);
                buttonElement.Click();
                StatusText.Text = "Skipped to previous track";
                Log.Information("Clicked skip back button");
            }
            catch (OpenQA.Selenium.WebDriverTimeoutException)
            {
                Log.Warning("Could not find skip back button");
                StatusText.Text = "Could not find skip back button";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error clicking skip back button");
            StatusText.Text = "Error skipping track";
        }
    }

    private async void ArtistInfoButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button button && button.DataContext is Artist.ArtistEntry entry)
            {
                // Extract artist ID from the URL
                var artistId = entry.ArtistStationUrl.Split('/').Last();
                
                // Read the bearer token from ArtistAuth.txt
                string bearerToken = "";
                if (File.Exists("Artist/ArtistAuth.txt"))
                {
                    bearerToken = await File.ReadAllTextAsync("Artist/ArtistAuth.txt");
                }
                else
                {
                    StatusText.Text = "No auth token found";
                    return;
                }

                // Make the API call
                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("Authorization", bearerToken);
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

                    var response = await httpClient.GetAsync($"https://api.edge-gateway.siriusxm.com/page/v1/page/artist-station/{artistId}");
                    var responseBody = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        // Format the JSON response with proper indentation
                        var jsonElement = JsonSerializer.Deserialize<JsonElement>(responseBody);
                        var formattedJson = JsonSerializer.Serialize(jsonElement, new JsonSerializerOptions 
                        { 
                            WriteIndented = true 
                        });

                        // Save the formatted JSON to ArtistInfo.json
                        await File.WriteAllTextAsync("Artist/ArtistInfo.json", formattedJson);
                        StatusText.Text = "Artist info saved successfully";
                        Log.Information("Artist info saved to Artist/ArtistInfo.json");

                        // Show the ArtistInfo window with the artist entry
                        var artistInfoWindow = new Artist.Artistinfo(entry);
                        artistInfoWindow.Owner = this;
                        artistInfoWindow.Show();
                    }
                    else
                    {
                        StatusText.Text = "Failed to fetch artist info";
                        Log.Error("Failed to fetch artist info. Status code: {StatusCode}", response.StatusCode);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error fetching artist info";
            Log.Error(ex, "Error fetching artist info");
        }
    }

    private async void LyricsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var lyricsWindow = LyricsWindow.GetInstance();
            lyricsWindow.Owner = this;
            
            if (currentTrack != null)
            {
                lyricsWindow.SetTrackInfo(
                    currentTrack.TrackName ?? "Unknown Track",
                    currentTrack.StationName ?? "Unknown Station",
                    currentTrack.AlbumArtUrl
                );

                // Show loading state
                lyricsWindow.SetLyrics("Fetching lyrics...");
                lyricsWindow.Show();
                lyricsWindow.Activate();

                // Delete existing lyrics.txt if it exists
                const string lyricsFilePath = "lyrics.txt";
                if (File.Exists(lyricsFilePath))
                {
                    try
                    {
                        File.Delete(lyricsFilePath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Could not delete existing lyrics.txt: {ex.Message}");
                    }
                }

                // Construct the search query
                string searchQuery = currentTrack.TrackName ?? "Unknown Track";
                
                // Try to extract artist name from track name (format: "Artist - Title")
                string[] parts = searchQuery.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    // If we have both artist and title, use them directly
                    searchQuery = searchQuery; // Already in correct format
                }
                else
                {
                    // If we don't have the format, just use the track name
                    searchQuery = currentTrack.TrackName ?? "Unknown Track";
                }

                // Run LyricsFetch.exe
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "LyricsFetch.exe",
                    Arguments = $"-- -S \"{searchQuery}\" -o",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = new System.Diagnostics.Process { StartInfo = startInfo })
                {
                    process.Start();
                    
                    // Wait for lyrics.txt to be created
                    int attempts = 0;
                    const int maxAttempts = 30; // 30 seconds timeout
                    
                    while (!File.Exists(lyricsFilePath) && attempts < maxAttempts)
                    {
                        await Task.Delay(1000); // Wait 1 second between checks
                        attempts++;
                    }

                    if (File.Exists(lyricsFilePath))
                    {
                        // Wait a bit more to ensure file is completely written
                        await Task.Delay(500);
                        
                        string lyrics = await File.ReadAllTextAsync(lyricsFilePath);
                        lyricsWindow.SetLyrics(lyrics);
                    }
                    else
                    {
                        lyricsWindow.SetLyrics("No lyrics found (timeout waiting for lyrics.txt)");
                    }
                }
            }
            else
            {
                lyricsWindow.SetTrackInfo("No Track", "No Station");
                lyricsWindow.SetLyrics("No track currently playing");
                lyricsWindow.Show();
                lyricsWindow.Activate();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error opening lyrics window");
            StatusText.Text = "Error opening lyrics window";
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
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

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; }

    [JsonPropertyName("request")]
    public PerformanceRequest Request { get; set; }
}

public class PerformanceRequest
{
    [JsonPropertyName("postData")]
    public string PostData { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; }
}

public class PerformanceResponse
{
    [JsonPropertyName("url")]
    public string Url { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; }
}

public class StreamEntry
{
    public string Timestamp { get; set; }
    public string StreamType { get; set; }
    public string Url { get; set; }
    public string TrackName { get; set; }
    public string ArtistName { get; set; }
    public string PreferredImageUrl { get; set; }
    public bool CanPlay => StreamType == "mp4" || StreamType == "mp3" || StreamType == "m3u8";

    public static string DecodeImageUrl(string imagePath)
    {
        try
        {
            var jsonObject = new
            {
                key = imagePath,
                edits = new object[]
                {
                    new
                    {
                        format = new
                        {
                            type = "jpeg"
                        }
                    },
                    new
                    {
                        resize = new
                        {
                            width = 1080,
                            height = 1080
                        }
                    }
                }
            };

            string jsonString = JsonSerializer.Serialize(jsonObject);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(jsonString);
            string base64String = Convert.ToBase64String(bytes);
            return "https://imgsrv-sxm-prod-device.streaming.siriusxm.com/" + base64String;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error decoding image URL for path: {Path}", imagePath);
            return null;
        }
    }
}

public class NowPlaying
{
    public string TrackName { get; set; }
    public string StationName { get; set; }
    public string AlbumArtUrl { get; set; }
}