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
    private enum FeaturePanel
    {
        Streams,
        WhatsNext,
        Artists,
        Lyrics
    }

    private const double BaseWidth = 1600;
    private const double BaseHeight = 920;
    private const double MinScale = 0.75;
    private const double MaxScale = 1.0;
    private const int ControlClickDebounceMs = 500;

    private readonly ObservableCollection<StreamEntry> _streamEntries;
    private readonly ObservableCollection<ArtistEntry> _artistEntries;
    private readonly StreamNetworkProcessor _streamProcessor = new();
    private readonly WebViewPlaybackService _playbackService = new();
    private readonly PlaybackMetadataTracker _metadataTracker = new();
    private readonly LiveQueueTracker _liveQueueTracker = new();
    private readonly DispatcherTimer _nowPlayingTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _liveQueueRefreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    private WebViewSessionService? _sessionService;
    private WebViewNetworkMonitor? _networkMonitor;
    private SuperNetworkLogger? _superLogger;
    private ArtistStations? _artistStations;
    private FileSystemWatcher? _stationFeedbackWatcher;

    private NowPlaying _currentTrack = new();
    /// <summary>Album art URL read from the WebView player DOM — matches the in-browser thumbnail.</summary>
    private string? _browserAlbumArtUrl;
    private bool _isMonitoring;
    private bool _isPaused;
    private bool _captureBearer;
    private bool _autoLoginStarted;
    private FeaturePanel _activePanel = FeaturePanel.Streams;
    private int _lyricsFetchToken;
    private string? _lyricsFetchedForTrack;
    private string? _lyricsDisplayedKey;
    private string? _lyricsDisplayedTrackName;
    private string? _lyricsDisplayedArtist;
    private string? _lyricsDisplayedArtUrl;
    private LiveCutEntry? _pendingLiveLyricsCut;
    private string? _lastLiveNowPlayingKey;
    private System.Diagnostics.Process? _activeLyricsProcess;
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
        WhatsNextListView.ItemsSource = _liveQueueTracker.DisplayEntries;
        _liveQueueTracker.Changed += OnLiveQueueChanged;

        AttemptAutoLoginWithCredsAsync = HandleAutoLoginAsync;

        SetupLogging();
        _nowPlayingTimer.Tick += async (_, _) => await UpdateNowPlayingAsync();
        _liveQueueRefreshTimer.Tick += async (_, _) => await RefreshLiveQueueAsync();
        SetupStationFeedbackWatcher();
        UpdateResponsiveLayout();
        SetTabState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Dispatcher UiDispatcher => Dispatcher;

    public ObservableCollection<StreamEntry> StreamEntries => _streamEntries;

    public PlaybackMetadataTracker MetadataTracker => _metadataTracker;

    public LiveQueueTracker LiveQueueTracker => _liveQueueTracker;

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
        if (_activePanel == FeaturePanel.Streams)
            return;

        _activePanel = FeaturePanel.Streams;
        SetTabState();
    }

    private void WhatsNextTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activePanel == FeaturePanel.WhatsNext)
            return;

        _activePanel = FeaturePanel.WhatsNext;
        SetTabState();
        RefreshWhatsNextHeader();
        _ = RefreshLiveQueueAsync();
    }

    private void OnLiveQueueChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            RefreshWhatsNextHeader();
            UpdateLiveQueueRefreshTimer();

            if (!_liveQueueTracker.IsLiveActive)
            {
                _lastLiveNowPlayingKey = null;
                ClearLiveLyricsNextTrackOffer();
                return;
            }

            var liveKey = BuildLiveNowPlayingKey(_liveQueueTracker.GetNowPlayingCut());
            if (liveKey == _lastLiveNowPlayingKey)
                return;

            _lastLiveNowPlayingKey = liveKey;
            Log.Information("Live radio now playing changed — {LiveKey}", liveKey ?? "(none)");
            _ = ApplyLiveRadioNowPlayingAsync();
        });
    }

    private void UpdateLiveQueueRefreshTimer()
    {
        if (IsMonitoring && _liveQueueTracker.IsLiveActive)
            _liveQueueRefreshTimer.Start();
        else
            _liveQueueRefreshTimer.Stop();
    }

    private async Task RefreshLiveQueueAsync()
    {
        if (!IsMonitoring || !_liveQueueTracker.IsLiveActive)
            return;

        _liveQueueTracker.RefreshPositions();

        try
        {
            await _streamProcessor.RefreshLiveLookAroundAsync(this);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Live queue lookAround refresh failed");
        }
    }

    private void ArtistsTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activePanel == FeaturePanel.Artists)
            return;

        _activePanel = FeaturePanel.Artists;
        SetTabState();
    }

    private void LyricsTabButton_Click(object sender, RoutedEventArgs e)
    {
        var wasAlreadyOnLyrics = _activePanel == FeaturePanel.Lyrics;

        _activePanel = FeaturePanel.Lyrics;
        SetTabState();

        if (wasAlreadyOnLyrics)
            return;

        // The now-playing track may have changed while this tab wasn't visible
        // (auto-refresh only fires while the Lyrics panel is the active one),
        // so catch up here instead of showing stale lyrics until manually reopened.
        if (_liveQueueTracker.IsLiveActive &&
            !string.IsNullOrEmpty(_lyricsDisplayedKey) &&
            !string.IsNullOrEmpty(_lyricsFetchedForTrack))
        {
            TryOfferLiveNextTrackLyrics();
            UpdateLiveLyricsHeaderForPinnedTrack();
        }
        else if (_lyricsFetchedForTrack != GetActiveLyricsTrackKey())
            _ = FetchAndDisplayLyricsAsync();
        else
            SyncLyricsHeaderFromNowPlaying();
    }

    private string? GetActiveLyricsTrackKey()
    {
        if (_liveQueueTracker.IsLiveActive)
        {
            var cut = _liveQueueTracker.GetNowPlayingCut();
            if (cut != null && !string.IsNullOrWhiteSpace(cut.TrackName))
                return cut.TrackName.Trim();
        }

        return _currentTrack.TrackName;
    }

    private void SetTabState()
    {
        StreamListView.Visibility = _activePanel == FeaturePanel.Streams ? Visibility.Visible : Visibility.Collapsed;
        WhatsNextPanel.Visibility = _activePanel == FeaturePanel.WhatsNext ? Visibility.Visible : Visibility.Collapsed;
        ArtistsPanel.Visibility = _activePanel == FeaturePanel.Artists ? Visibility.Visible : Visibility.Collapsed;
        LyricsPanel.Visibility = _activePanel == FeaturePanel.Lyrics ? Visibility.Visible : Visibility.Collapsed;
        ClearStreamsButton.Visibility = _activePanel == FeaturePanel.Streams ? Visibility.Visible : Visibility.Collapsed;

        var accentBlue = (Brush)FindResource("AccentBlue");
        var textSecondary = (Brush)FindResource("TextSecondary");

        StreamsTabButton.Background = _activePanel == FeaturePanel.Streams ? accentBlue : Brushes.Transparent;
        StreamsTabButton.Foreground = _activePanel == FeaturePanel.Streams ? Brushes.White : textSecondary;

        WhatsNextTabButton.Background = _activePanel == FeaturePanel.WhatsNext ? accentBlue : Brushes.Transparent;
        WhatsNextTabButton.Foreground = _activePanel == FeaturePanel.WhatsNext ? Brushes.White : textSecondary;

        ArtistsTabButton.Background = _activePanel == FeaturePanel.Artists ? accentBlue : Brushes.Transparent;
        ArtistsTabButton.Foreground = _activePanel == FeaturePanel.Artists ? Brushes.White : textSecondary;

        LyricsTabButton.Background = _activePanel == FeaturePanel.Lyrics ? accentBlue : Brushes.Transparent;
        LyricsTabButton.Foreground = _activePanel == FeaturePanel.Lyrics ? Brushes.White : textSecondary;
    }

    private void RefreshWhatsNextHeader()
    {
        var channel = _liveQueueTracker.ActiveChannel;
        if (channel == null)
        {
            WhatsNextChannelText.Text = "No live channel";
            WhatsNextShowText.Text = IsMonitoring
                ? "Tune a live radio station (e.g. Hip-Hop Nation, The Heat) — queue updates every ~30s"
                : "Start monitoring, then tune a live radio station";
            WhatsNextEmptyState.Visibility = Visibility.Visible;
            WhatsNextListView.Visibility = Visibility.Collapsed;
            return;
        }

        var channelLabel = channel.ChannelNumber is > 0
            ? $"{channel.ChannelName} · Ch {channel.ChannelNumber}"
            : channel.ChannelName;

        WhatsNextChannelText.Text = channelLabel;

        var upNext = _liveQueueTracker.UpNextCount;
        var queueHint = upNext > 0
            ? $"{upNext} track{(upNext == 1 ? "" : "s")} up next · refreshes every ~30s"
            : "Live queue updates from lookAround (~30s)";

        WhatsNextShowText.Text = string.IsNullOrWhiteSpace(channel.ShowName)
            ? queueHint
            : $"Show: {channel.ShowName} · {queueHint}";

        var hasEntries = _liveQueueTracker.DisplayEntries.Count > 0;
        WhatsNextEmptyState.Visibility = hasEntries ? Visibility.Collapsed : Visibility.Visible;
        WhatsNextListView.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
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
                UpdateLiveQueueRefreshTimer();
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
                _liveQueueRefreshTimer.Stop();
                _isPaused = false;
                ResetPauseButtonIcons();

                NowPlayingTrack.Text = "No track playing";
                NowPlayingStation.Text = "No station selected";
                NowPlayingArt.Source = null;
                NowPlayingMeta.Text = string.Empty;
                NowPlayingMeta.Visibility = Visibility.Collapsed;
                _currentTrack = new NowPlaying();
                _metadataTracker.Reset();
                _liveQueueTracker.Clear();
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
            if (_liveQueueTracker.IsLiveActive)
                _liveQueueTracker.RefreshPositions();

            var playing = _metadataTracker.HasRecentMetadata(TimeSpan.FromMinutes(30))
                ? _metadataTracker.Snapshot()
                : new NowPlaying();

            if (_liveQueueTracker.IsLiveActive)
                TryApplyLiveRadioNowPlaying(playing);

            var domPlaying = await _playbackService.GetNowPlayingAsync(core);
            _browserAlbumArtUrl = domPlaying.AlbumArtUrl;
            MergeDomNowPlayingFallback(playing, domPlaying);

            if (_liveQueueTracker.IsLiveActive)
                ApplyLiveRadioAlbumArt(playing);
            else
                EnrichNowPlayingFromCapturedMetadata(playing);

            if (IsEmptyNowPlaying(playing))
                return;

            if (playing.TrackName == _currentTrack.TrackName &&
                playing.StationName == _currentTrack.StationName &&
                playing.AlbumArtUrl == _currentTrack.AlbumArtUrl &&
                playing.AlbumName == _currentTrack.AlbumName &&
                playing.DurationMs == _currentTrack.DurationMs)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() => ApplyNowPlayingToUi(playing));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error updating now playing information");
        }
    }

    private static bool IsEmptyTrackName(string? trackName) =>
        string.IsNullOrWhiteSpace(trackName) ||
        trackName == "No track playing";

    private static bool IsEmptyNowPlaying(NowPlaying playing) =>
        IsEmptyTrackName(playing.TrackName);

    private static void MergeDomNowPlayingFallback(NowPlaying playing, NowPlaying domPlaying)
    {
        if (IsEmptyNowPlaying(playing) && !IsEmptyNowPlaying(domPlaying))
            playing.TrackName = domPlaying.TrackName;

        if (string.IsNullOrWhiteSpace(playing.StationName) ||
            playing.StationName == "No station selected")
        {
            if (!string.IsNullOrWhiteSpace(domPlaying.StationName) &&
                domPlaying.StationName != "No station selected")
            {
                playing.StationName = domPlaying.StationName;
            }
        }

        // Prefer live browser player art — it matches the thumbnail shown in the WebView.
        if (!string.IsNullOrEmpty(domPlaying.AlbumArtUrl))
            playing.AlbumArtUrl = domPlaying.AlbumArtUrl;
    }

    private void ApplyNowPlayingToUi(NowPlaying playing)
    {
        var trackChanged = playing.TrackName != _currentTrack.TrackName;

        if (trackChanged)
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
            NowPlayingArt.Source = LoadAlbumArtImage(playing.AlbumArtUrl);
            if (NowPlayingArt.Source != null)
                Log.Debug("Updated album art image");
            else if (!string.IsNullOrEmpty(playing.AlbumArtUrl))
                Log.Error("Error loading album art image from URL: {Url}", playing.AlbumArtUrl);
        }

        if (trackChanged && _activePanel == FeaturePanel.Lyrics && !IsEmptyTrackName(playing.TrackName))
        {
            if (_liveQueueTracker.IsLiveActive && TryOfferLiveNextTrackLyrics())
            {
                UpdateLiveLyricsHeaderForPinnedTrack();
            }
            else
            {
                Log.Information("Now playing changed while lyrics panel is open — auto-refreshing lyrics for {TrackName}", playing.TrackName);
                _ = FetchAndDisplayLyricsAsync();
            }
        }
        else if (_activePanel == FeaturePanel.Lyrics)
        {
            if (_liveQueueTracker.IsLiveActive && !string.IsNullOrEmpty(_lyricsDisplayedKey))
                UpdateLiveLyricsHeaderForPinnedTrack();
            else
                SyncLyricsHeaderFromNowPlaying();
        }
    }

    private static string BuildLyricsTrackKey(string? artist, string? track) =>
        string.IsNullOrWhiteSpace(track) ? string.Empty : $"{artist?.Trim()}|{track.Trim()}";

    private static string BuildLyricsTrackKey(LiveCutEntry? cut) =>
        cut == null ? string.Empty : BuildLyricsTrackKey(cut.ArtistName, cut.TrackName);

    /// <summary>
    /// Live radio only: queue moved ahead while user is reading current lyrics — offer next track without switching.
    /// </summary>
    private bool TryOfferLiveNextTrackLyrics()
    {
        if (!_liveQueueTracker.IsLiveActive || string.IsNullOrEmpty(_lyricsDisplayedKey))
            return false;

        var cut = _liveQueueTracker.GetNowPlayingCut();
        if (cut == null || string.IsNullOrWhiteSpace(cut.TrackName))
            return false;

        var nextKey = BuildLyricsTrackKey(cut);
        if (nextKey == _lyricsDisplayedKey)
        {
            ClearLiveLyricsNextTrackOffer();
            return false;
        }

        _pendingLiveLyricsCut = cut;
        ShowLiveLyricsNextTrackBanner(cut);
        Log.Information("Live radio lyrics deferred — keeping {CurrentKey}, next available: {NextKey}", _lyricsDisplayedKey, nextKey);
        return true;
    }

    private void ShowLiveLyricsNextTrackBanner(LiveCutEntry cut)
    {
        LiveLyricsNextTrackText.Text = cut.DisplayLine;
        LiveLyricsNextTrackBanner.Visibility = Visibility.Visible;
        UpdateLyricsBodyLayoutForBanner();
    }

    private void ClearLiveLyricsNextTrackOffer()
    {
        _pendingLiveLyricsCut = null;
        LiveLyricsNextTrackBanner.Visibility = Visibility.Collapsed;
        UpdateLyricsBodyLayoutForBanner();
    }

    private void UpdateLyricsBodyLayoutForBanner()
    {
        LyricsBodyText.Margin = LiveLyricsNextTrackBanner.Visibility == Visibility.Visible
            ? new Thickness(30, 78, 30, 28)
            : new Thickness(30, 28, 30, 28);
    }

    private void UpdateLiveLyricsHeaderForPinnedTrack()
    {
        if (!string.IsNullOrEmpty(_lyricsDisplayedTrackName))
        {
            SetLyricsTrackInfo(
                _lyricsDisplayedTrackName,
                _lyricsDisplayedArtist ?? "Unknown Artist",
                _lyricsDisplayedArtUrl);
            return;
        }

        SyncLyricsHeaderFromNowPlaying();
    }

    private void SetPinnedLyricsDisplay(string trackName, string artistName, string? artUrl)
    {
        _lyricsDisplayedKey = BuildLyricsTrackKey(artistName, trackName);
        _lyricsDisplayedTrackName = trackName;
        _lyricsDisplayedArtist = artistName;
        _lyricsDisplayedArtUrl = artUrl;
        SetLyricsTrackInfo(trackName, artistName, artUrl);
    }

    private void LiveLyricsNextTrackButton_Click(object sender, RoutedEventArgs e)
    {
        var cut = _pendingLiveLyricsCut ?? _liveQueueTracker.GetNowPlayingCut();
        ClearLiveLyricsNextTrackOffer();
        _ = FetchAndDisplayLyricsAsync(liveCutOverride: cut);
    }

    /// <summary>
    /// Keeps the lyrics panel header in sync with the main now-playing footer.
    /// </summary>
    private void SyncLyricsHeaderFromNowPlaying()
    {
        var stationName = _currentTrack.StationName ?? "No station selected";
        SetLyricsTrackInfo(
            _currentTrack.TrackName ?? "No track playing",
            ResolveLyricsDisplayArtist(stationName),
            ResolveNowPlayingAlbumArtUrl());
    }

    /// <summary>
    /// Prefer the live browser player thumbnail; fall back to captured metadata when DOM art is unavailable.
    /// For live radio, prefer track art from the What's Next queue over the channel logo in the browser.
    /// </summary>
    private string? ResolveNowPlayingAlbumArtUrl()
    {
        if (_liveQueueTracker.IsLiveActive)
        {
            var cutArt = _liveQueueTracker.GetNowPlayingCut()?.ImageUrl;
            if (!string.IsNullOrEmpty(cutArt))
                return cutArt;
        }

        return !string.IsNullOrEmpty(_browserAlbumArtUrl) ? _browserAlbumArtUrl : _currentTrack.AlbumArtUrl;
    }

    private static string? BuildLiveNowPlayingKey(LiveCutEntry? cut) =>
        cut == null || string.IsNullOrWhiteSpace(cut.TrackName)
            ? null
            : $"{cut.ArtistName}|{cut.TrackName}|{cut.ValidFromUtc:O}";

    /// <summary>
    /// Overlays track/artist from the live radio What's Next queue onto now playing.
    /// </summary>
    private bool TryApplyLiveRadioNowPlaying(NowPlaying playing)
    {
        var cut = _liveQueueTracker.GetNowPlayingCut();
        if (cut == null || string.IsNullOrWhiteSpace(cut.TrackName))
            return false;

        playing.TrackName = cut.TrackName.Trim();

        var channel = _liveQueueTracker.ActiveChannel?.ChannelName;
        if (!string.IsNullOrWhiteSpace(channel))
            playing.StationName = channel.Trim();

        ApplyLiveRadioAlbumArt(playing, cut);
        return true;
    }

    private void ApplyLiveRadioAlbumArt(NowPlaying playing, LiveCutEntry? cut = null)
    {
        cut ??= _liveQueueTracker.GetNowPlayingCut();
        if (!string.IsNullOrEmpty(cut?.ImageUrl))
            playing.AlbumArtUrl = cut.ImageUrl;
    }

    private async Task ApplyLiveRadioNowPlayingAsync()
    {
        var core = _sessionService?.CoreWebView2;
        if (core == null || !IsMonitoring || !_liveQueueTracker.IsLiveActive)
            return;

        try
        {
            _liveQueueTracker.RefreshPositions();

            var playing = _metadataTracker.HasRecentMetadata(TimeSpan.FromMinutes(30))
                ? _metadataTracker.Snapshot()
                : new NowPlaying();

            if (!TryApplyLiveRadioNowPlaying(playing))
                return;

            var domPlaying = await _playbackService.GetNowPlayingAsync(core);
            _browserAlbumArtUrl = domPlaying.AlbumArtUrl;
            MergeDomNowPlayingFallback(playing, domPlaying);
            ApplyLiveRadioAlbumArt(playing);

            if (IsEmptyNowPlaying(playing))
                return;

            await Dispatcher.InvokeAsync(() => ApplyNowPlayingToUi(playing));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error applying live radio now playing from What's Next queue");
        }
    }

    private string ResolveLyricsDisplayArtist(string stationName)
    {
        if (_liveQueueTracker.IsLiveActive)
        {
            var liveArtist = _liveQueueTracker.GetNowPlayingCut()?.ArtistName;
            if (!string.IsNullOrWhiteSpace(liveArtist))
                return liveArtist.Trim();
        }

        return stationName;
    }

    private LyricsArtistResolution ResolveLyricsArtist(string trackName, string stationName)
    {
        if (_liveQueueTracker.IsLiveActive)
            return LyricsSearchQueryBuilder.ResolveArtistForLiveRadio(_liveQueueTracker.GetNowPlayingCut());

        return LyricsSearchQueryBuilder.ResolveArtist(trackName, stationName, _streamEntries);
    }

    /// <summary>
    /// Fill gaps from captured tuneSource/peek stream entries. Works even when the
    /// mobile player hides title/station text in the DOM.
    /// </summary>
    private void EnrichNowPlayingFromCapturedMetadata(NowPlaying playing)
    {
        StreamEntry? match = null;

        if (!IsEmptyTrackName(playing.TrackName))
        {
            match = StreamEntries.LastOrDefault(e =>
                string.Equals(e.TrackName, playing.TrackName, StringComparison.OrdinalIgnoreCase));
        }

        match ??= StreamEntries.LastOrDefault(e =>
            !string.IsNullOrWhiteSpace(e.TrackName) &&
            e.TrackName is not ("Unnamed MP4" or "Unnamed MP3" or "Unnamed M3U8"));

        if (match == null)
            return;

        if (IsEmptyTrackName(playing.TrackName))
            playing.TrackName = match.TrackName;

        if (string.IsNullOrWhiteSpace(playing.StationName) ||
            playing.StationName == "No station selected")
        {
            var trackerStation = _metadataTracker.Snapshot().StationName;
            if (!string.IsNullOrWhiteSpace(trackerStation) &&
                trackerStation != "No station selected")
            {
                playing.StationName = trackerStation;
            }
            else if (!string.IsNullOrWhiteSpace(match.ArtistName))
            {
                playing.StationName = match.ArtistName;
            }
        }

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

    private void LyricsButton_Click(object sender, RoutedEventArgs e)
    {
        LyricsTabButton.Visibility = Visibility.Visible;
        _activePanel = FeaturePanel.Lyrics;
        SetTabState();

        _ = FetchAndDisplayLyricsAsync();
    }

    /// <summary>
    /// Fetches lyrics for whatever track is currently in <see cref="_currentTrack"/> and renders
    /// them into the in-window Lyrics panel. Guarded by a token so that if the now-playing track
    /// changes again while a fetch is in flight, the stale result is discarded instead of
    /// overwriting the UI for the newer track.
    /// </summary>
    private async Task FetchAndDisplayLyricsAsync(LiveCutEntry? liveCutOverride = null)
    {
        var token = ++_lyricsFetchToken;
        string? searchQuery = null;
        bool IsStale() => token != _lyricsFetchToken;

        try
        {
            var liveCutForFetch = liveCutOverride
                ?? (_liveQueueTracker.IsLiveActive ? _liveQueueTracker.GetNowPlayingCut() : null);
            var hasLiveTrack = liveCutForFetch != null &&
                               !string.IsNullOrWhiteSpace(liveCutForFetch.TrackName);

            if ((_currentTrack == null || IsEmptyTrackName(_currentTrack.TrackName)) && !hasLiveTrack)
            {
                Log.Information("Lyrics requested but no track is currently playing");
                LyricsFetchLogger.LogNoTrack();
                _lyricsFetchedForTrack = null;
                SetLyricsTrackInfo("No Track", "No Station", null);
                ShowLyricsEmptyState("No track currently playing");
                return;
            }

            var trackName = hasLiveTrack
                ? liveCutForFetch!.TrackName.Trim()
                : _currentTrack.TrackName ?? "Unknown Track";
            var stationName = _currentTrack.StationName ?? "Unknown Station";

            if (_liveQueueTracker.IsLiveActive)
            {
                var channel = _liveQueueTracker.ActiveChannel?.ChannelName;
                if (!string.IsNullOrWhiteSpace(channel))
                    stationName = channel.Trim();
            }

            var artistResolution = liveCutOverride != null
                ? LyricsSearchQueryBuilder.ResolveArtistForLiveRadio(liveCutOverride)
                : ResolveLyricsArtist(trackName, stationName);
            var artistName = artistResolution.Artist ?? string.Empty;
            var displayArtist = !string.IsNullOrWhiteSpace(artistName)
                ? artistName
                : ResolveLyricsDisplayArtist(stationName);
            var displayArtUrl = liveCutOverride?.ImageUrl
                ?? (hasLiveTrack ? liveCutForFetch!.ImageUrl : null)
                ?? ResolveNowPlayingAlbumArtUrl();

            if (IsStale())
                return;

            if (IsProcessStillRunning(_activeLyricsProcess))
            {
                try
                {
                    Log.Debug("Killing previous in-flight LyricsFetch process (track changed before it finished)");
                    _activeLyricsProcess!.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not kill previous LyricsFetch process");
                }
            }
            _activeLyricsProcess = null;

            SetLyricsTrackInfo(trackName, displayArtist, displayArtUrl);
            ShowLyricsLoadingState();
            ClearLiveLyricsNextTrackOffer();

            var workingDirectory = Environment.CurrentDirectory;
            var lyricsFetchPath = ResolveLyricsFetchPath();
            var exeDirectory = IOPath.GetDirectoryName(lyricsFetchPath) ?? workingDirectory;
            var lyricsFileInCwd = IOPath.Combine(workingDirectory, "lyrics.txt");
            var lyricsFileInExeDir = IOPath.Combine(exeDirectory, "lyrics.txt");

            var queryDetails = LyricsSearchQueryBuilder.BuildWithDetails(trackName, artistName);
            searchQuery = queryDetails.Query;

            if (string.IsNullOrWhiteSpace(searchQuery))
            {
                Log.Warning("Lyrics fetch skipped — could not build a search query for track {TrackName}", trackName);
                ShowLyricsEmptyState("No track currently playing");
                return;
            }

            Log.Information(
                "Lyrics fetch started — Track: {TrackName}, Artist: {ArtistName}, ArtistSource: {ArtistSource}, Station: {StationName}, SearchQuery: {SearchQuery}, QuerySource: {QuerySource}, Exe: {ExePath}, WorkingDir: {WorkingDir}, ExpectedOutput: {OutputInCwd} or {OutputInExeDir}, LogFile: {LogFile}",
                trackName, artistName, artistResolution.Source, stationName, searchQuery, queryDetails.Source, lyricsFetchPath, workingDirectory, lyricsFileInCwd, lyricsFileInExeDir, LyricsFetchLogger.LogFilePath);

            var fetchArguments = $"-- -S \"{searchQuery}\" -o";
            LyricsFetchLogger.LogFetchStarted(
                trackName, artistName, artistResolution.Source, stationName, searchQuery, queryDetails.Source,
                lyricsFetchPath, fetchArguments, exeDirectory,
                lyricsFileInCwd, lyricsFileInExeDir);

            foreach (var existingPath in new[] { lyricsFileInCwd, lyricsFileInExeDir })
            {
                if (!File.Exists(existingPath))
                    continue;

                try
                {
                    File.Delete(existingPath);
                    Log.Debug("Deleted existing lyrics file at {Path}", existingPath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not delete existing lyrics file at {Path}", existingPath);
                }
            }

            if (!File.Exists(lyricsFetchPath))
            {
                Log.Error(
                    "LyricsFetch.exe not found — checked BaseDirectory/Lyrics, CWD/Lyrics, and CWD. WorkingDir: {WorkingDir}",
                    workingDirectory);
                LyricsFetchLogger.LogExeNotFound(workingDirectory, lyricsFetchPath);
                if (!IsStale())
                {
                    ShowLyricsEmptyState("Lyrics fetch failed: LyricsFetch.exe not found");
                    StatusText.Text = "Lyrics fetch failed: LyricsFetch.exe not found";
                }
                return;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = lyricsFetchPath,
                Arguments = fetchArguments,
                WorkingDirectory = exeDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Log.Debug("Starting LyricsFetch — FileName: {FileName}, Arguments: {Arguments}, WorkingDirectory: {WorkingDirectory}",
                startInfo.FileName, startInfo.Arguments, startInfo.WorkingDirectory);

            using var process = new System.Diagnostics.Process { StartInfo = startInfo };

            try
            {
                process.Start();
                _activeLyricsProcess = process;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start LyricsFetch process at {ExePath}", lyricsFetchPath);
                LyricsFetchLogger.LogProcessStartFailed(trackName, searchQuery, lyricsFetchPath, ex);
                if (!IsStale())
                {
                    ShowLyricsEmptyState($"Lyrics fetch failed: {ex.Message}");
                    StatusText.Text = "Lyrics fetch failed";
                }
                return;
            }

            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                var attempts = 0;
                const int maxAttempts = 30;

                while (attempts < maxAttempts)
                {
                    var foundInCwd = File.Exists(lyricsFileInCwd);
                    var foundInExeDir = File.Exists(lyricsFileInExeDir);

                    if (foundInCwd || foundInExeDir)
                    {
                        Log.Debug("Lyrics file detected after {Attempt}s — InCwd: {InCwd}, InExeDir: {InExeDir}",
                            attempts + 1, foundInCwd, foundInExeDir);
                        break;
                    }

                    if (process.HasExited)
                    {
                        Log.Debug("LyricsFetch exited before output file appeared — ExitCode: {ExitCode}, Attempt: {Attempt}",
                            process.ExitCode, attempts + 1);
                        break;
                    }

                    await Task.Delay(1000);
                    attempts++;
                }

                if (!process.HasExited)
                {
                    var exited = process.WaitForExit(5000);
                    if (!exited)
                        Log.Warning("LyricsFetch did not exit within 5s after polling finished");
                }

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                Log.Information(
                    "LyricsFetch finished — ExitCode: {ExitCode}, Waited: {Attempts}s, StdOut: {StdOut}, StdErr: {StdErr}",
                    process.ExitCode, attempts, TrimLogText(stdout), TrimLogText(stderr));

                var resolvedLyricsPath = File.Exists(lyricsFileInCwd) ? lyricsFileInCwd
                    : File.Exists(lyricsFileInExeDir) ? lyricsFileInExeDir
                    : null;

                if (resolvedLyricsPath != null)
                {
                    await Task.Delay(500);
                    var lyrics = await File.ReadAllTextAsync(resolvedLyricsPath);

                    if (IsStale())
                    {
                        Log.Debug("Discarding lyrics result for {TrackName} — a newer track is now active", trackName);
                        LyricsFetchLogger.LogDiscardedStaleResult(trackName, searchQuery);
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(lyrics))
                    {
                        Log.Warning("Lyrics file was empty at {Path}", resolvedLyricsPath);
                        LyricsFetchLogger.LogEmptyLyricsFile(
                            trackName, artistName, stationName, searchQuery, resolvedLyricsPath,
                            process.ExitCode, stdout, stderr);
                        ShowLyricsEmptyState("No lyrics found for this track");
                        StatusText.Text = "No lyrics found";
                        return;
                    }

                    Log.Information("Lyrics loaded from {Path} ({CharCount} characters)", resolvedLyricsPath, lyrics.Length);
                    LyricsFetchLogger.LogFetchSucceeded(
                        trackName, artistName, stationName, searchQuery,
                        process.ExitCode, attempts, stdout, stderr,
                        resolvedLyricsPath, lyrics.Length);
                    _lyricsFetchedForTrack = trackName;
                    ShowLyricsText(lyrics);
                    SetPinnedLyricsDisplay(trackName, displayArtist, displayArtUrl);
                    if (_liveQueueTracker.IsLiveActive)
                        TryOfferLiveNextTrackLyrics();
                    return;
                }

                Log.Warning(
                    "Lyrics fetch failed — no output file after {Attempts}s. ExitCode: {ExitCode}, Checked: {PathInCwd}, {PathInExeDir}, StdOut: {StdOut}, StdErr: {StdErr}",
                    attempts, process.ExitCode, lyricsFileInCwd, lyricsFileInExeDir, TrimLogText(stdout), TrimLogText(stderr));

                LyricsFetchLogger.LogFetchFailed(
                    trackName, artistName, stationName, searchQuery,
                    process.ExitCode, attempts, stdout, stderr,
                    lyricsFileInCwd, lyricsFileInExeDir);

                if (!IsStale())
                {
                    ShowLyricsEmptyState("No lyrics found for this track");
                    StatusText.Text = "Lyrics fetch failed";
                    UpdateLiveLyricsHeaderForPinnedTrack();
                }
            }
            finally
            {
                // The process is disposed once this `using` scope ends; drop the shared
                // reference so the next fetch never touches a disposed Process object.
                if (ReferenceEquals(_activeLyricsProcess, process))
                    _activeLyricsProcess = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error during lyrics fetch");
            LyricsFetchLogger.LogUnexpectedError(_currentTrack?.TrackName, searchQuery, ex);
            if (!IsStale())
            {
                ShowLyricsEmptyState("Something went wrong fetching lyrics");
                StatusText.Text = "Error fetching lyrics";
            }
        }
    }

    private static bool IsProcessStillRunning(System.Diagnostics.Process? process)
    {
        if (process == null)
            return false;

        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // Thrown when the process was never associated or has already been disposed.
            return false;
        }
    }

    private void SetLyricsTrackInfo(string trackName, string stationName, string? albumArtUrl)
    {
        LyricsTrackNameText.Text = trackName;
        LyricsArtistNameText.Text = stationName;
        LyricsAlbumArt.Source = LoadAlbumArtImage(albumArtUrl);
    }

    private static BitmapImage? LoadAlbumArtImage(string? albumArtUrl)
    {
        if (string.IsNullOrEmpty(albumArtUrl))
            return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(albumArtUrl);
            image.EndInit();
            return image;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error loading album art from URL: {Url}", albumArtUrl);
            return null;
        }
    }

    private void ShowLyricsLoadingState()
    {
        LyricsLoadingState.Visibility = Visibility.Visible;
        LyricsEmptyState.Visibility = Visibility.Collapsed;
        LyricsBodyText.Text = "";
        LyricsBodyText.Visibility = Visibility.Collapsed;
    }

    private void ShowLyricsEmptyState(string message)
    {
        LyricsLoadingState.Visibility = Visibility.Collapsed;
        LyricsBodyText.Visibility = Visibility.Collapsed;
        LyricsEmptyStateText.Text = message;
        LyricsEmptyState.Visibility = Visibility.Visible;
    }

    private void ShowLyricsText(string lyrics)
    {
        LyricsLoadingState.Visibility = Visibility.Collapsed;
        LyricsEmptyState.Visibility = Visibility.Collapsed;
        LyricsBodyText.Text = lyrics.Trim();
        LyricsBodyText.Visibility = Visibility.Visible;
    }

    private static string ResolveLyricsFetchPath()
    {
        var candidates = new[]
        {
            IOPath.Combine(AppContext.BaseDirectory, "Lyrics", "LyricsFetch.exe"),
            IOPath.Combine(Environment.CurrentDirectory, "Lyrics", "LyricsFetch.exe"),
            IOPath.Combine(Environment.CurrentDirectory, "LyricsFetch.exe")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return candidates[0];
    }

    private static string TrimLogText(string? text, int maxLength = 500)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "(empty)";

        var normalized = text.Trim().Replace("\r\n", "\\n").Replace('\n', '\\').Replace('\r', '\\');
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
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
