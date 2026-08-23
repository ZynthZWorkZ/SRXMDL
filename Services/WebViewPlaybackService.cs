using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Serilog;
using SRXMDL.Models;

namespace SRXMDL.Services;

public sealed class WebViewPlaybackService
{
    private bool _isPaused;

    public async Task<NowPlaying> GetNowPlayingAsync(CoreWebView2 core)
    {
        const string script = """
            (function() {
                var track = document.querySelector('div.styles-module__title___D3wQt');
                var station = document.querySelector('div.styles-module__text___xT9yv span');
                var art = document.querySelector('div.styles-module__imageContainer___b-ipU img');
                return JSON.stringify({
                    trackName: track ? track.textContent.trim() : '',
                    stationName: station ? station.textContent.trim() : '',
                    albumArtUrl: art ? art.src : ''
                });
            })();
            """;

        try
        {
            var result = await core.ExecuteScriptAsync(script);
            if (string.IsNullOrWhiteSpace(result) || result == "null")
                return CreateDefaultNowPlaying();

            var jsonString = JsonSerializer.Deserialize<string>(result);
            if (string.IsNullOrWhiteSpace(jsonString))
                return CreateDefaultNowPlaying();

            var json = JsonSerializer.Deserialize<JsonElement>(jsonString);
            return new NowPlaying
            {
                TrackName = GetStringOrDefault(json, "trackName", "No track playing"),
                StationName = GetStringOrDefault(json, "stationName", "No station selected"),
                AlbumArtUrl = json.TryGetProperty("albumArtUrl", out var artProp)
                    ? artProp.GetString()
                    : null
            };
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error reading now playing from WebView2");
            return CreateDefaultNowPlaying();
        }
    }

    public async Task<bool> TogglePauseAsync(CoreWebView2 core)
    {
        var shouldPause = !_isPaused;
        var label = shouldPause ? "Pause" : "Play";
        var pausePathPrefix = "M7 2.754a2 2 0 0 0-2 2v14.492";
        var playPathPrefix = "M20.692 10.702c1 .577";

        var script = $$"""
            (function() {
                function isVisible(el) {
                    if (!el) return false;
                    var rect = el.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0 && !el.disabled;
                }

                function clickButton(btn) {
                    if (!isVisible(btn)) return false;
                    btn.scrollIntoView({ behavior: 'smooth', block: 'center' });
                    btn.click();
                    return true;
                }

                var label = {{JsonSerializer.Serialize(label)}};
                var btn = document.querySelector("button[aria-label='" + label + "']");
                if (clickButton(btn)) return true;

                var buttons = Array.from(document.querySelectorAll('button'));
                btn = buttons.find(function(b) {
                    var aria = b.getAttribute('aria-label') || '';
                    return aria.toLowerCase().indexOf(label.toLowerCase()) >= 0;
                });
                if (clickButton(btn)) return true;

                var pathPrefix = {{JsonSerializer.Serialize(shouldPause ? pausePathPrefix : playPathPrefix)}};
                var path = document.querySelector("svg path[d*='" + pathPrefix + "']");
                if (path) {
                    var ancestor = path.closest('button');
                    if (clickButton(ancestor)) return true;
                }

                return false;
            })();
            """;

        return await ClickWithRetriesAsync(core, script, shouldPause ? "play/pause" : "play/pause", success =>
        {
            if (success)
                _isPaused = shouldPause;
        });
    }

    public Task<bool> SkipForwardAsync(CoreWebView2 core) =>
        ClickTransportButtonAsync(core, """
            (function() {
                function isVisible(el) {
                    if (!el) return false;
                    var rect = el.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0 && !el.disabled;
                }

                function clickButton(btn) {
                    if (!isVisible(btn)) return false;
                    btn.scrollIntoView({ behavior: 'smooth', block: 'center' });
                    btn.click();
                    return true;
                }

                var terms = ['next', 'forward', 'skip'];
                var buttons = Array.from(document.querySelectorAll('button'));
                var btn = buttons.find(function(b) {
                    var aria = (b.getAttribute('aria-label') || '').toLowerCase();
                    return terms.some(function(t) { return aria.indexOf(t) >= 0; });
                });
                if (clickButton(btn)) return true;

                var path = document.querySelector("svg[viewBox='0 0 24 24'] path[d*='M16.757 4.626'], svg path[d*='M16.757']");
                if (path && clickButton(path.closest('button'))) return true;

                var buttons = Array.from(document.querySelectorAll("button[class*='forward'], button[class*='next'], button[class*='skip']"));
                var match = buttons.find(isVisible);
                return clickButton(match);
            })();
            """, "forward");

    public Task<bool> SkipBackAsync(CoreWebView2 core) =>
        ClickTransportButtonAsync(core, """
            (function() {
                function isVisible(el) {
                    if (!el) return false;
                    var rect = el.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0 && !el.disabled;
                }

                function clickButton(btn) {
                    if (!isVisible(btn)) return false;
                    btn.scrollIntoView({ behavior: 'smooth', block: 'center' });
                    btn.click();
                    return true;
                }

                var terms = ['previous', 'back', 'rewind'];
                var buttons = Array.from(document.querySelectorAll('button'));
                var btn = buttons.find(function(b) {
                    var aria = (b.getAttribute('aria-label') || '').toLowerCase();
                    return terms.some(function(t) { return aria.indexOf(t) >= 0; });
                });
                if (clickButton(btn)) return true;

                var path = document.querySelector("svg[viewBox='0 0 24 24'] path[d*='M7.764 4.554'], svg path[d*='M7.764']");
                if (path && clickButton(path.closest('button'))) return true;

                var buttons = Array.from(document.querySelectorAll("button[class*='back'], button[class*='previous'], button[class*='rewind']"));
                var match = buttons.find(isVisible);
                return clickButton(match);
            })();
            """, "skip back");

    private static async Task<bool> ClickTransportButtonAsync(CoreWebView2 core, string script, string label)
        => await ClickWithRetriesAsync(core, script, label);

    private static async Task<bool> ClickWithRetriesAsync(
        CoreWebView2 core,
        string script,
        string label,
        Action<bool>? onSuccess = null)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var result = await core.ExecuteScriptAsync(script);
                if (string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
                {
                    onSuccess?.Invoke(true);
                    Log.Information("Successfully clicked {Label} button", label);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Attempt {Attempt} to click {Label} button failed", attempt + 1, label);
            }

            await Task.Delay(300 * (attempt + 1));
        }

        Log.Warning("Could not find {Label} button after multiple attempts", label);
        return false;
    }

    private static NowPlaying CreateDefaultNowPlaying() => new()
    {
        TrackName = "No track playing",
        StationName = "No station selected",
        AlbumArtUrl = null
    };

    private static string GetStringOrDefault(JsonElement element, string propertyName, string defaultValue)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return defaultValue;

        var value = prop.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }
}
