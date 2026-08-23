using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;
using SRXMDL.Login;

namespace SRXMDL.Services;

public sealed class WebViewSessionService : IAsyncDisposable
{
    private const string LoginUrl = "https://www.siriusxm.com/player/login";
    private const string HomeUrl = "https://www.siriusxm.com/player/home";
    private static readonly string[] CookieSources =
    [
        "https://www.siriusxm.com",
        "https://siriusxm.com",
        "https://api.edge-gateway.siriusxm.com"
    ];

    private WebView2? _webView;
    private bool _isLoggedIn;

    public event Action<string>? StatusChanged;
    public event Action<bool>? LoginStateChanged;
    public event Action<string?>? SourceChanged;

    public CoreWebView2? CoreWebView2 => _webView?.CoreWebView2;
    public bool IsInitialized => CoreWebView2 != null;
    public bool IsLoggedIn => _isLoggedIn;

    public async Task InitializeAsync(WebView2 webView)
    {
        _webView = webView;

        var userDataFolder = Path.Combine(AppContext.BaseDirectory, "WebView2Data", "Main");
        Directory.CreateDirectory(userDataFolder);

        var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await webView.EnsureCoreWebView2Async(environment);

        var core = webView.CoreWebView2!;
        core.Settings.AreDefaultScriptDialogsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;

        core.NavigationCompleted += OnNavigationCompleted;
        core.SourceChanged += OnSourceChanged;

        if (CookieFileHelper.Exists() && HasAuthCookie(await GetAllCookiesAsync(core)))
        {
            SetStatus("Restoring saved session...");
            core.Navigate(HomeUrl);
        }
        else
        {
            SetStatus("Sign in with your SiriusXM account below.");
            core.Navigate(LoginUrl);
        }
    }

    public void NavigateToLogin()
    {
        EnsureReady();
        CoreWebView2!.Navigate(LoginUrl);
        SetStatus("Opened SiriusXM login page.");
    }

    public void NavigateToHome()
    {
        EnsureReady();
        CoreWebView2!.Navigate(HomeUrl);
    }

    public void Reload()
    {
        EnsureReady();
        CoreWebView2!.Reload();
    }

    public async Task<bool> TryAutoLoginWithSavedCredentialsAsync()
    {
        EnsureReady();

        var cred = CredentialStore.Load();
        if (cred == null)
        {
            SetStatus("Save email and password first, then try auto sign-in.");
            return false;
        }

        var bearerPath = Path.Combine(AppContext.BaseDirectory, "Login", "authorization Bearer.txt");
        if (!File.Exists(bearerPath))
        {
            SetStatus("Waiting for SiriusXM page load so a bearer token can be captured...");
            CoreWebView2!.Navigate(LoginUrl);
            return false;
        }

        var bearer = (await File.ReadAllTextAsync(bearerPath)).Trim();
        if (string.IsNullOrWhiteSpace(bearer))
        {
            SetStatus("Bearer token file is empty. Reload the login page and try again.");
            return false;
        }

        SetStatus("Signing in with saved credentials...");
        using var loginService = new SiriusXmLoginService();
        var session = await loginService.LoginAsync(
            cred.Value.Email,
            cred.Value.Password,
            bearer.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase));

        if (session == null)
        {
            SetStatus("Auto sign-in failed. Check credentials or sign in manually.");
            return false;
        }

        await InjectCookiesAsync(loginService.ExportCookieEntries());
        await SaveSessionCookiesAsync();
        CoreWebView2!.Navigate(HomeUrl);
        SetStatus("Signed in with saved credentials.");
        return true;
    }

    public async Task SaveSessionCookiesAsync()
    {
        EnsureReady();
        var cookies = await GetAllCookiesAsync(CoreWebView2!);
        await CookieFileHelper.SaveAsync(cookies);
        Log.Information("WebView2 session cookies saved to {Path}", CookieFileHelper.CookieFilePath);
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (CoreWebView2 == null || !e.IsSuccess)
            return;

        SourceChanged?.Invoke(CoreWebView2.Source);
        await UpdateLoginStateAsync(CoreWebView2.Source);
    }

    private async void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (CoreWebView2 == null)
            return;

        SourceChanged?.Invoke(CoreWebView2.Source);
        await UpdateLoginStateAsync(CoreWebView2.Source);
    }

    private async Task UpdateLoginStateAsync(string? url)
    {
        var loggedIn = IsLoggedInUrl(url);
        if (loggedIn == _isLoggedIn)
            return;

        _isLoggedIn = loggedIn;
        LoginStateChanged?.Invoke(loggedIn);

        if (!loggedIn)
        {
            SetStatus("Sign in with your SiriusXM account below.");
            return;
        }

        try
        {
            await SaveSessionCookiesAsync();
            SetStatus("Signed in. Session saved for monitoring.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save WebView2 session cookies");
            SetStatus("Signed in, but saving session cookies failed.");
        }
    }

    private async Task<List<CookieExportEntry>> GetAllCookiesAsync(CoreWebView2 core)
    {
        var cookies = new List<CookieExportEntry>();

        foreach (var source in CookieSources)
        {
            var domainCookies = await core.CookieManager.GetCookiesAsync(source);
            foreach (var cookie in domainCookies)
            {
                cookies.Add(new CookieExportEntry
                {
                    Domain = cookie.Domain.StartsWith('.') ? cookie.Domain : "." + cookie.Domain.TrimStart('.'),
                    Path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                    Name = cookie.Name,
                    Value = cookie.Value,
                    Expires = cookie.Expires,
                    IsSecure = cookie.IsSecure
                });
            }
        }

        return cookies;
    }

    private async Task InjectCookiesAsync(IEnumerable<CookieExportEntry> cookies)
    {
        EnsureReady();
        var manager = CoreWebView2!.CookieManager;

        foreach (var cookie in cookies)
        {
            var domain = cookie.Domain.TrimStart('.');
            var webCookie = manager.CreateCookie(cookie.Name, cookie.Value, domain, cookie.Path);
            webCookie.IsSecure = cookie.IsSecure;
            if (cookie.Expires.HasValue)
                webCookie.Expires = cookie.Expires.Value;

            manager.AddOrUpdateCookie(webCookie);
        }

        await Task.CompletedTask;
    }

    private static bool IsLoggedInUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!url.Contains("siriusxm.com/player", StringComparison.OrdinalIgnoreCase))
            return false;

        if (url.Contains("/login", StringComparison.OrdinalIgnoreCase))
            return false;

        if (url.Contains("/welcome", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool HasAuthCookie(IEnumerable<CookieExportEntry> cookies) =>
        cookies.Any(c => c.Name.Equals("AUTH_TOKEN", StringComparison.OrdinalIgnoreCase));

    private void EnsureReady()
    {
        if (CoreWebView2 == null)
            throw new InvalidOperationException("WebView2 is not initialized.");
    }

    private void SetStatus(string message) => StatusChanged?.Invoke(message);

    public ValueTask DisposeAsync()
    {
        if (CoreWebView2 != null)
        {
            CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            CoreWebView2.SourceChanged -= OnSourceChanged;
        }

        _webView = null;
        return ValueTask.CompletedTask;
    }
}
