using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Serilog;
using SRXMDL.Artist;
using SRXMDL.Download;
using SRXMDL.Login;
using SRXMDL.Lyrics;
using SRXMDL.Models;
using SRXMDL.Services;
using IOPath = System.IO.Path;

namespace SRXMDL;

public partial class MainWindow : Window, IStreamCaptureHost, INotifyPropertyChanged
{
    private const double BaseWidth = 1600;
    private const double BaseHeight = 920;
    private const double MinScale = 0.75;
    private const double MaxScale = 1.0;
    private const int ControlClickDebounceMs = 500;

    private readonly ObservableCollection<StreamEntry> _streamEntries;
    private readonly ObservableCollection<ArtistEntry> _artistEntries;
    private readonly StreamNetworkProcessor _streamProcessor = new();
    private readonly WebViewPlaybackService _playbackService = new();
    private readonly DispatcherTimer _nowPlayingTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private WebViewSessionService? _sessionService;
    private WebViewNetworkMonitor? _networkMonitor;
    private SuperNetworkLogger? _superLogger;
    private ArtistStations? _artistStations;
    private FileSystemWatcher? _stationFeedbackWatcher;

    private NowPlaying _currentTrack = new();
    private bool _isMonitoring;
    private bool _isPaused;
    private bool _captureBearer;
    private bool _autoLoginStarted;
    private bool _showingArtists;
    private bool _webExpanded;
    private GridLength _savedPlayerColumnWidth = new(1, GridUnitType.Star);
    private GridLength _savedFeatureColumnWidth = new(1, GridUnitType.Star);
    private DateTime _lastControlClick = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _streamEntries = new ObservableCollection<StreamEntry>();
        _artistEntries = new ObservableCollection<ArtistEntry>();
        StreamListView.ItemsSource = _streamEntries;
        ArtistListView.ItemsSource = _artistEntries;

        AttemptAutoLoginWithCredsAsync = HandleAutoLoginAsync;

        SetupLogging();
        _nowPlayingTimer.Tick += async (_, _) => await UpdateNowPlayingAsync();
        SetupStationFeedbackWatcher();
        UpdateResponsiveLayout();
        SetTabState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Dispatcher UiDispatcher => Dispatcher;

    public ObservableCollection<StreamEntry> StreamEntries => _streamEntries;

    public string? LastTuneSourceUrl { get; set; }
    public string? LastTuneSourcePayload { get; set; }
    public string? LastTuneSourceAuthToken { get; set; }

    public bool CaptureBearer
    {
        get => _captureBearer;
        set => _captureBearer = value;
    }

    public Func<string, Task>? AttemptAutoLoginWithCredsAsync { get; set; }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (_isMonitoring == value)
                return;

            _isMonitoring = value;
            _artistStations?.SetMonitoringStatus(value);
            OnPropertyChanged();
        }
    }

    public void UpdateTotalCapturedCount()
    {
        Dispatcher.Invoke(() => TotalCapturedCount.Text = _streamEntries.Count.ToString());
    }

    public void RefreshStreamList()
    {
        Dispatcher.Invoke(() => StreamListView.Items.Refresh());
    }

    public void SetStatus(string message)
    {
        Dispatcher.Invoke(() => StatusText.Text = message);
    }

    public bool IsDuplicateStream(string url, out StreamEntry? existingEntry)
    {
        var fileName = ExtractFileNameFromUrl(url);
        existingEntry = _streamEntries.FirstOrDefault(entry => ExtractFileNameFromUrl(entry.Url) == fileName);
        return existingEntry != null;
    }

    public Task ProcessArtistStationUrlAsync(string url)
    {
        if (_artistStations == null)
            return Task.CompletedTask;

        return _artistStations.ProcessArtistStationUrl(url);
    }

    public Task RunOnUiAsync(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.InvokeAsync(action).Task;
    }

    private void SetupLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File("mp4_requests.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();
    }

    private void SetupStationFeedbackWatcher()
    {
        try
        {
            Directory.CreateDirectory("Stations");
            _stationFeedbackWatcher = new FileSystemWatcher("Stations")
            {
                Filter = "station_feedback.json",
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _stationFeedbackWatcher.Changed += OnStationFeedbackChanged;
            Log.Information("Station feedback file watcher initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error setting up station feedback watcher");
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateResponsiveLayout();

        try
        {
            _sessionService = new WebViewSessionService();
            _sessionService.StatusChanged += message => Dispatcher.Invoke(() =>
            {
                StatusText.Text = message;
            });
            _sessionService.LoginStateChanged += signedIn => Dispatcher.Invoke(() =>
            {
                PlayerStatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                    signedIn ? "#30D158" : "#FF453A")!);
                PlayerStatusText.Text = signedIn ? "Signed in" : "Not signed in";
            });
            _sessionService.SourceChanged += async url =>
            {
                if (string.IsNullOrWhiteSpace(url))
                    return;

                if (url.StartsWith("https://www.siriusxm.com/player/artist-station", StringComparison.OrdinalIgnoreCase))
                    await ProcessArtistStationUrlAsync(url);
            };

            await _sessionService.InitializeAsync(SxmWebView);

            var core = _sessionService.CoreWebView2;
            if (core != null)
            {
                _networkMonitor = new WebViewNetworkMonitor(this, _streamProcessor);
                await _networkMonitor.StartAsync(core);
                Log.Information("WebView2 CDP network monitoring attached at startup");
            }

            _artistStations = new ArtistStations(
                () => _sessionService.CoreWebView2,
                Dispatcher,
                _artistEntries)
            {
                ArtistListView = ArtistListView
            };

            StatusText.Text = "Player ready — sign in on the left to begin";
            Log.Information("WebView2 session initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize WebView2 session");
            StatusText.Text = "Failed to initialize player";
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ClearCacheNormalState.Visibility = Visibility.Visible;
        ClearCacheConfirmState.Visibility = Visibility.Collapsed;
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        ClearCacheNormalState.Visibility = Visibility.Collapsed;
        ClearCacheConfirmState.Visibility = Visibility.Visible;
    }

    private void CancelClearCache_Click(object sender, RoutedEventArgs e)
    {
        ClearCacheNormalState.Visibility = Visibility.Visible;
        ClearCacheConfirmState.Visibility = Visibility.Collapsed;
    }

    private async void ConfirmClearCache_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        await RestartAndClearCacheAsync();
    }

    private async Task RestartAndClearCacheAsync()
    {
        try
        {
            StatusText.Text = "Clearing cache and restarting...";
            Log.Information("User requested WebView2 cache clear; restarting application");

            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                StatusText.Text = "Could not locate application executable";
                return;
            }

            var browserPid = _sessionService?.CoreWebView2?.BrowserProcessId;

            if (_superLogger is { IsRecording: true })
                await _superLogger.StopAndSaveAsync();

            // Close the WebView2 session synchronously (and stop the CDP monitor) before
            // spawning the watcher process, so the browser process actually releases the
            // user data folder instead of racing against our own process exit.
            _networkMonitor?.Stop();
            if (_sessionService != null)
                await _sessionService.DisposeAsync();

            var arguments = $"--clear-cache-after-pid {Environment.ProcessId}";
            if (browserPid.HasValue)
                arguments += $" --clear-cache-browser-pid {browserPid.Value}";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true
            });

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restart application for cache clear");
            StatusText.Text = "Failed to clear cache";
        }
    }

    private async void SuperLogButton_Click(object sender, RoutedEventArgs e)
    {
        var core = _sessionService?.CoreWebView2;

        try
        {
            if (_superLogger is { IsRecording: true })
            {
                SuperLogTitleText.Text = "Saving...";
                var path = await _superLogger.StopAndSaveAsync();

                SuperLogTitleText.Text = "Start Super Log";
                SuperLogSubtitleText.Text = "Capture full web traffic for debugging";
                SuperLogRecDot.Visibility = Visibility.Collapsed;

                StatusText.Text = $"Super log saved to {path}";
                Log.Information("Super log saved to {Path}", path);

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not open explorer for super log path");
                }
            }
            else if (core != null)
            {
                _superLogger ??= new SuperNetworkLogger();
                _superLogger.EntryCountChanged += OnSuperLogEntryCountChanged;
                await _superLogger.StartAsync(core);

                SuperLogTitleText.Text = "Stop Super Log";
                SuperLogSubtitleText.Text = "Recording... 0 events captured";
                SuperLogRecDot.Visibility = Visibility.Visible;

                StatusText.Text = "Super log recording started";
                Log.Information("Super log recording started");
            }
            else
            {
                StatusText.Text = "Player is not ready yet";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error toggling super log");
            StatusText.Text = "Error with super log";
        }

        SettingsPopup.IsOpen = false;
    }

    private void OnSuperLogEntryCountChanged(int count)
    {
        Dispatcher.Invoke(() =>
        {
            if (_superLogger is { IsRecording: true })
                SuperLogSubtitleText.Text = $"Recording... {count} events captured";
        });
    }

    private void ExpandWebButton_Click(object sender, RoutedEventArgs e)
    {
        _webExpanded = !_webExpanded;

        if (_webExpanded)
        {
            _savedPlayerColumnWidth = PlayerColumn.Width;
            _savedFeatureColumnWidth = FeatureColumn.Width;

            FeatureColumn.MinWidth = 0;
            FeatureColumn.Width = new GridLength(0);

            ExpandWebButton.Content = "\uE73F";
            ExpandWebButton.ToolTip = "Restore split view";
        }
        else
        {
            FeatureColumn.MinWidth = 280;
            FeatureColumn.Width = _savedFeatureColumnWidth.Value > 0 ? _savedFeatureColumnWidth : new GridLength(1, GridUnitType.Star);
            PlayerColumn.Width = _savedPlayerColumnWidth.Value > 0 ? _savedPlayerColumnWidth : new GridLength(1, GridUnitType.Star);

            ExpandWebButton.Content = "\uE740";
            ExpandWebButton.ToolTip = "Expand player";
        }
    }

    private void StreamsTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_showingArtists)
            return;

        _showingArtists = false;
        SetTabState();
    }

    private void ArtistsTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (_showingArtists)
            return;

        _showingArtists = true;
        SetTabState();
    }

    private void SetTabState()
    {
        StreamListView.Visibility = _showingArtists ? Visibility.Collapsed : Visibility.Visible;
        ArtistsPanel.Visibility = _showingArtists ? Visibility.Visible : Visibility.Collapsed;
        ClearStreamsButton.Visibility = _showingArtists ? Visibility.Collapsed : Visibility.Visible;

        var accentBlue = (Brush)FindResource("AccentBlue");
        var textSecondary = (Brush)FindResource("TextSecondary");

        StreamsTabButton.Background = _showingArtists ? Brushes.Transparent : accentBlue;
        StreamsTabButton.Foreground = _showingArtists ? textSecondary : Brushes.White;

        ArtistsTabButton.Background = _showingArtists ? accentBlue : Brushes.Transparent;
        ArtistsTabButton.Foreground = _showingArtists ? Brushes.White : textSecondary;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        try
        {
            var widthRatio = ActualWidth / BaseWidth;
            var heightRatio = ActualHeight / BaseHeight;
            var scale = Math.Min(MaxScale, Math.Max(MinScale, Math.Min(widthRatio, heightRatio)));

            RootScale.ScaleX = scale;
            RootScale.ScaleY = scale;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error applying responsive layout");
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsMonitoring)
            return;

        if (_sessionService?.CoreWebView2 == null)
        {
            StatusText.Text = "Player is not ready yet";
            return;
        }

        try
        {
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusText.Text = "Starting...";

            _streamEntries.Clear();
            UpdateTotalCapturedCount();

            foreach (var entry in _artistEntries.Where(a => !a.IsFavorite).ToList())
                _artistEntries.Remove(entry);

            var cookiesImported = CookieFileHelper.Exists();
            CaptureBearer = !cookiesImported;

            // The CDP network monitor is attached once at startup (see Window_Loaded) so that it
            // never misses traffic due to the async attach delay racing with playback that may
            // already be in progress. Starting/stopping here only toggles whether captured
            // traffic gets recorded to the visible list.
            UpdateMonitoringStatus(true);

            StatusText.Text = cookiesImported
                ? "Monitoring active — using saved session"
                : "Monitoring active — sign in in the player if needed";

            Log.Information("Stream capture recording started");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred while starting the monitor");
            StatusText.Text = "Error occurred";
            await StopMonitoringAsync();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopMonitoringAsync();
    }

    private async Task StopMonitoringAsync()
    {
        if (!IsMonitoring)
            return;

        try
        {
            // Note: the CDP network monitor itself keeps running in the background (it is only
            // torn down when the app closes) so that no traffic is missed between Stop/Start.

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

    private void UpdateMonitoringStatus(bool isActive)
    {
        Dispatcher.Invoke(() =>
        {
            IsMonitoring = isActive;

            if (isActive)
            {
                StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#30D158")!);
                StatusIndicator.Effect = new DropShadowEffect
                {
                    Color = (Color)ColorConverter.ConvertFromString("#30D158")!,
                    Opacity = 0.7,
                    BlurRadius = 8,
                    ShadowDepth = 0
                };
                ConnectionStatus.Text = "ACTIVE";

                if (FindResource("BlinkAnimation") is Storyboard blinkAnimation)
                    blinkAnimation.Begin(StatusIndicator);

                _nowPlayingTimer.Start();
            }
            else
            {
                StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF453A")!);
                StatusIndicator.Effect = new DropShadowEffect
                {
                    Color = (Color)ColorConverter.ConvertFromString("#FF453A")!,
                    Opacity = 0.7,
                    BlurRadius = 8,
                    ShadowDepth = 0
                };
                ConnectionStatus.Text = "STANDBY";

                StatusIndicator.BeginAnimation(UIElement.OpacityProperty, null);
                StatusIndicator.Opacity = 1;

                _nowPlayingTimer.Stop();
                _isPaused = false;
                ResetPauseButtonIcons();

                NowPlayingTrack.Text = "No track playing";
                NowPlayingStation.Text = "No station selected";
                NowPlayingArt.Source = null;
                NowPlayingMeta.Text = string.Empty;
                NowPlayingMeta.Visibility = Visibility.Collapsed;
                _currentTrack = new NowPlaying();
            }
        });
    }

    private async Task UpdateNowPlayingAsync()
    {
        var core = _sessionService?.CoreWebView2;
        if (core == null || !IsMonitoring)
            return;

        try
        {
            var playing = await _playbackService.GetNowPlayingAsync(core);
            EnrichNowPlayingFromCapturedMetadata(playing);

            if (playing.TrackName == _currentTrack.TrackName &&
                playing.StationName == _currentTrack.StationName &&
                playing.AlbumArtUrl == _currentTrack.AlbumArtUrl &&
                playing.AlbumName == _currentTrack.AlbumName &&
                playing.DurationMs == _currentTrack.DurationMs)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (playing.TrackName != _currentTrack.TrackName)
                {
                    _currentTrack.TrackName = playing.TrackName;
                    NowPlayingTrack.Text = playing.TrackName;
                    Log.Debug("Updated track name to: {TrackName}", playing.TrackName);
                }

                if (playing.StationName != _currentTrack.StationName)
                {
                    _currentTrack.StationName = playing.StationName;
                    NowPlayingStation.Text = playing.StationName;
                    Log.Debug("Updated station name to: {StationName}", playing.StationName);
                }

                if (playing.AlbumName != _currentTrack.AlbumName || playing.DurationMs != _currentTrack.DurationMs)
                {
                    _currentTrack.AlbumName = playing.AlbumName;
                    _currentTrack.DurationMs = playing.DurationMs;

                    var metaParts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(playing.AlbumName))
                        metaParts.Add(playing.AlbumName);
                    if (!string.IsNullOrEmpty(playing.DurationFormatted))
                        metaParts.Add(playing.DurationFormatted);

                    if (metaParts.Count > 0)
                    {
                        NowPlayingMeta.Text = string.Join(" · ", metaParts);
                        NowPlayingMeta.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        NowPlayingMeta.Text = string.Empty;
                        NowPlayingMeta.Visibility = Visibility.Collapsed;
                    }
                }

                if (playing.AlbumArtUrl != _currentTrack.AlbumArtUrl)
                {
                    _currentTrack.AlbumArtUrl = playing.AlbumArtUrl;
                    if (!string.IsNullOrEmpty(playing.AlbumArtUrl))
                    {
                        try
                        {
                            NowPlayingArt.Source = new BitmapImage(new Uri(playing.AlbumArtUrl));
                            Log.Debug("Updated album art image");
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error loading album art image from URL: {Url}", playing.AlbumArtUrl);
                        }
                    }
                    else
                    {
                        NowPlayingArt.Source = null;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error updating now playing information");
        }
    }

    /// <summary>
    /// The DOM-scraped now-playing title/art is fragile and lacks album/duration data.
    /// Cross-reference it against the rich metadata already captured from the
    /// tuneSource/peek API responses (see StreamNetworkProcessor) to fill the gaps
    /// and prefer the higher-resolution API artwork when the page hasn't rendered any.
    /// </summary>
    private void EnrichNowPlayingFromCapturedMetadata(NowPlaying playing)
    {
        if (string.IsNullOrWhiteSpace(playing.TrackName) || playing.TrackName == "No track playing")
            return;

        var match = StreamEntries.LastOrDefault(e =>
            string.Equals(e.TrackName, playing.TrackName, StringComparison.OrdinalIgnoreCase));

        if (match == null)
            return;

        playing.AlbumName ??= match.AlbumName;
        playing.DurationMs ??= match.DurationMs;

        if (string.IsNullOrEmpty(playing.AlbumArtUrl) && !string.IsNullOrEmpty(match.PreferredImageUrl))
            playing.AlbumArtUrl = match.PreferredImageUrl;
    }

    private async Task HandleAutoLoginAsync(string bearerToken)
    {
        if (_autoLoginStarted || _sessionService == null)
            return;

        _autoLoginStarted = true;

        try
        {
            var loginDir = IOPath.Combine(AppContext.BaseDirectory, "Login");
            Directory.CreateDirectory(loginDir);
            await File.WriteAllTextAsync(IOPath.Combine(loginDir, "authorization Bearer.txt"), bearerToken.Trim());

            if (await _sessionService.TryAutoLoginWithSavedCredentialsAsync())
            {
                CaptureBearer = false;
                SetStatus("Signed in with saved credentials");
                Log.Information("Auto login succeeded via WebView2 session");
            }
            else
            {
                Log.Warning("Auto login with saved credentials failed");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during auto login with captured credentials");
        }
    }

    private async void OnStationFeedbackChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            await Task.Delay(100);

            if (!string.IsNullOrEmpty(LastTuneSourceUrl) &&
                !string.IsNullOrEmpty(LastTuneSourcePayload) &&
                !string.IsNullOrEmpty(LastTuneSourceAuthToken))
            {
                Log.Information("Station feedback updated, sending tuneSource request");
                await _streamProcessor.SendTuneSourceRequestAsync(
                    LastTuneSourceUrl,
                    LastTuneSourcePayload,
                    LastTuneSourceAuthToken,
                    this);
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

    private static string ExtractFileNameFromUrl(string url)
    {
        try
        {
            var fileName = url.Split('/').Last();
            return fileName.Split('?')[0];
        }
        catch
        {
            return url;
        }
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

    private void ApplyNowPlaying_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button button && button.DataContext is StreamEntry entry)
            {
                var newTrack = string.IsNullOrWhiteSpace(_currentTrack.TrackName) ? entry.TrackName : _currentTrack.TrackName;
                var newArtist = string.IsNullOrWhiteSpace(_currentTrack.StationName) ? entry.ArtistName : _currentTrack.StationName;
                var newArt = string.IsNullOrWhiteSpace(_currentTrack.AlbumArtUrl) ? entry.PreferredImageUrl : _currentTrack.AlbumArtUrl;

                entry.TrackName = newTrack;
                entry.ArtistName = newArtist;
                entry.PreferredImageUrl = newArt;

                StreamListView.Items.Refresh();
                StatusText.Text = "Applied Now Playing metadata to selected stream.";
                Log.Information("Applied Now Playing metadata to stream: {Track} - {Artist}", newTrack, newArtist);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error applying Now Playing metadata");
            StatusText.Text = "Error applying Now Playing metadata";
        }
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new LoginWindow();
            win.Owner = this;
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error opening login window");
            StatusText.Text = "Error opening login window";
        }
    }

    private void ClearStreams_Click(object sender, RoutedEventArgs e)
    {
        _streamEntries.Clear();
        UpdateTotalCapturedCount();
        StatusText.Text = "Stream activity cleared";
        Log.Information("Stream activity cleared");
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is ArtistEntry entry)
            _artistStations?.ToggleFavorite(button, entry);
    }

    private async void PlayArtistStation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is ArtistEntry entry)
            await (_artistStations?.PlayArtistStation(entry, status => StatusText.Text = status) ?? Task.CompletedTask);
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((DateTime.Now - _lastControlClick).TotalMilliseconds < ControlClickDebounceMs)
            return;
        _lastControlClick = DateTime.Now;

        var core = _sessionService?.CoreWebView2;
        if (core == null || !IsMonitoring)
            return;

        if (sender is Button button)
            button.IsEnabled = false;

        try
        {
            var shouldPause = !_isPaused;
            var success = await _playbackService.TogglePauseAsync(core);

            if (!success)
            {
                StatusText.Text = "Could not find play/pause button";
                return;
            }

            _isPaused = shouldPause;
            UpdatePauseButtonIcons(shouldPause);
            StatusText.Text = shouldPause ? "Playback paused" : "Playback resumed";
            Log.Information("Successfully toggled playback: {State}", shouldPause ? "Paused" : "Resumed");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error toggling playback state");
            StatusText.Text = "Error toggling playback";
        }
        finally
        {
            if (sender is Button btn)
            {
                await Task.Delay(300);
                await Dispatcher.InvokeAsync(() => btn.IsEnabled = true);
            }
        }
    }

    private async void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if ((DateTime.Now - _lastControlClick).TotalMilliseconds < ControlClickDebounceMs)
            return;
        _lastControlClick = DateTime.Now;

        var core = _sessionService?.CoreWebView2;
        if (core == null || !IsMonitoring)
            return;

        if (sender is Button button)
            button.IsEnabled = false;

        try
        {
            var success = await _playbackService.SkipForwardAsync(core);
            StatusText.Text = success ? "Skipped to next track" : "Could not find forward button";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error clicking forward button");
            StatusText.Text = "Error skipping track";
        }
        finally
        {
            if (sender is Button btn)
            {
                await Task.Delay(300);
                await Dispatcher.InvokeAsync(() => btn.IsEnabled = true);
            }
        }
    }

    private async void SkipBackButton_Click(object sender, RoutedEventArgs e)
    {
        if ((DateTime.Now - _lastControlClick).TotalMilliseconds < ControlClickDebounceMs)
            return;
        _lastControlClick = DateTime.Now;

        var core = _sessionService?.CoreWebView2;
        if (core == null || !IsMonitoring)
            return;

        if (sender is Button button)
            button.IsEnabled = false;

        try
        {
            var success = await _playbackService.SkipBackAsync(core);
            StatusText.Text = success ? "Skipped to previous track" : "Could not find skip back button";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error clicking skip back button");
            StatusText.Text = "Error skipping track";
        }
        finally
        {
            if (sender is Button btn)
            {
                await Task.Delay(300);
                await Dispatcher.InvokeAsync(() => btn.IsEnabled = true);
            }
        }
    }

    private void PauseButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (PauseIcon != null)
            PauseIcon.Fill = Brushes.White;
        if (PlayIcon != null && PlayIcon.Visibility == Visibility.Visible)
            PlayIcon.Fill = Brushes.White;
    }

    private void PauseButton_MouseLeave(object sender, MouseEventArgs e)
    {
        var fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A1A1AA")!);
        if (PauseIcon != null && PauseIcon.Visibility == Visibility.Visible)
            PauseIcon.Fill = fill;
        if (PlayIcon != null && PlayIcon.Visibility == Visibility.Visible)
            PlayIcon.Fill = fill;
    }

    private void ForwardButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (ForwardIcon != null)
            ForwardIcon.Fill = Brushes.White;
    }

    private void ForwardButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (ForwardIcon != null)
            ForwardIcon.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A1A1AA")!);
    }

    private void SkipBackButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (SkipBackIcon != null)
            SkipBackIcon.Fill = Brushes.White;
    }

    private void SkipBackButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (SkipBackIcon != null)
            SkipBackIcon.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A1A1AA")!);
    }

    private void UpdatePauseButtonIcons(bool shouldPause)
    {
        if (PauseIcon == null || PlayIcon == null)
            return;

        var fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A1A1AA")!);
        if (shouldPause)
        {
            PauseIcon.Visibility = Visibility.Collapsed;
            PlayIcon.Visibility = Visibility.Visible;
            PlayIcon.Fill = fill;
        }
        else
        {
            PauseIcon.Visibility = Visibility.Visible;
            PlayIcon.Visibility = Visibility.Collapsed;
            PauseIcon.Fill = fill;
        }
    }

    private void ResetPauseButtonIcons()
    {
        if (PauseIcon == null || PlayIcon == null)
            return;

        var fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A1A1AA")!);
        PauseIcon.Visibility = Visibility.Visible;
        PlayIcon.Visibility = Visibility.Collapsed;
        PauseIcon.Fill = fill;
    }

    private async void ArtistInfoButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button button && button.DataContext is ArtistEntry entry)
            {
                var artistId = entry.ArtistStationUrl.Split('/').Last();

                string bearerToken;
                if (File.Exists("Artist/ArtistAuth.txt"))
                {
                    bearerToken = await File.ReadAllTextAsync("Artist/ArtistAuth.txt");
                }
                else
                {
                    StatusText.Text = "No auth token found";
                    return;
                }

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("Authorization", bearerToken);
                httpClient.DefaultRequestHeaders.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

                var response = await httpClient.GetAsync($"https://api.edge-gateway.siriusxm.com/page/v1/page/artist-station/{artistId}");
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var jsonElement = JsonSerializer.Deserialize<JsonElement>(responseBody);
                    var formattedJson = JsonSerializer.Serialize(jsonElement, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    Directory.CreateDirectory("Artist");
                    await File.WriteAllTextAsync("Artist/ArtistInfo.json", formattedJson);
                    StatusText.Text = "Artist info saved successfully";
                    Log.Information("Artist info saved to Artist/ArtistInfo.json");

                    var artistInfoWindow = new Artistinfo(entry);
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

            if (_currentTrack != null)
            {
                lyricsWindow.SetTrackInfo(
                    _currentTrack.TrackName ?? "Unknown Track",
                    _currentTrack.StationName ?? "Unknown Station",
                    _currentTrack.AlbumArtUrl);

                lyricsWindow.SetLyrics("Fetching lyrics...");
                lyricsWindow.Show();
                lyricsWindow.Activate();

                const string lyricsFilePath = "lyrics.txt";
                if (File.Exists(lyricsFilePath))
                {
                    try
                    {
                        File.Delete(lyricsFilePath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("Could not delete existing lyrics.txt: {Message}", ex.Message);
                    }
                }

                var searchQuery = _currentTrack.TrackName ?? "Unknown Track";
                var parts = searchQuery.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    searchQuery = _currentTrack.TrackName ?? "Unknown Track";

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "LyricsFetch.exe",
                    Arguments = $"-- -S \"{searchQuery}\" -o",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new System.Diagnostics.Process { StartInfo = startInfo };
                process.Start();

                var attempts = 0;
                const int maxAttempts = 30;

                while (!File.Exists(lyricsFilePath) && attempts < maxAttempts)
                {
                    await Task.Delay(1000);
                    attempts++;
                }

                if (File.Exists(lyricsFilePath))
                {
                    await Task.Delay(500);
                    var lyrics = await File.ReadAllTextAsync(lyricsFilePath);
                    lyricsWindow.SetLyrics(lyrics);
                }
                else
                {
                    lyricsWindow.SetLyrics("No lyrics found (timeout waiting for lyrics.txt)");
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
            DragMove();
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

    protected override async void OnClosed(EventArgs e)
    {
        _stationFeedbackWatcher?.Dispose();
        _networkMonitor?.Stop();

        if (_superLogger is { IsRecording: true })
        {
            try
            {
                var path = await _superLogger.StopAndSaveAsync();
                Log.Information("Super log auto-saved on app close: {Path}", path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to auto-save super log on app close");
            }
        }

        if (_sessionService != null)
            await _sessionService.DisposeAsync();

        await StopMonitoringAsync();
        ClearSensitiveFiles();

        Log.CloseAndFlush();
        base.OnClosed(e);
    }

    private void ClearSensitiveFiles()
    {
        try
        {
            ClearFileContents(IOPath.Combine("Login", "authorization Bearer.txt"));
            ClearFileContents(IOPath.Combine("Artist", "ArtistAuth.txt"));
            ClearFileContents(IOPath.Combine("HLSKey", "authorization Bearer.txt"));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ClearSensitiveFiles encountered an error");
        }
    }

    private static void ClearFileContents(string path)
    {
        try
        {
            if (File.Exists(path))
                File.WriteAllText(path, string.Empty);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to clear {Path}", path);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
