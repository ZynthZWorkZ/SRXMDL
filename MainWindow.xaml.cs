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

namespace SRXMDL;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private ChromeDriver? driver;
    private CancellationTokenSource? cancellationTokenSource;
    private bool isMonitoring = false;
    private ObservableCollection<StreamEntry> streamEntries;

    public MainWindow()
    {
        InitializeComponent();
        streamEntries = new ObservableCollection<StreamEntry>();
        StreamListView.ItemsSource = streamEntries;
        SetupLogging();
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

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (isMonitoring) return;

        try
        {
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusText.Text = "Starting...";
            isMonitoring = true;
            streamEntries.Clear();

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
        if (!isMonitoring) return;

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
            isMonitoring = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StatusText.Text = "Ready";
        }
    }

    private async Task MonitorNetworkTraffic(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
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
            else
            {
                Log.Information("No temp.json found, using existing cookie file");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during cookie management");
            throw;
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
        var button = sender as Button;
        var item = button.DataContext as StreamEntry;
        if (item != null)
        {
            var downloadWindow = new DownloadWindow(item.Url);
            downloadWindow.Owner = this;
            downloadWindow.ShowDialog();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        StopMonitoring().Wait();
        Log.CloseAndFlush();
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