using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace SRXMDL.Services;

public sealed class StreamProxyServer : IDisposable
{
    private const int MaxHlsSegments = 6;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Regex KeyUriRegex = new(
        @"(#EXT-X-KEY:[^\n]*URI="")[^""]*("")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagUriRegex = new(
        @"(URI="")([^""]*)("")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MediaSequenceRegex = new(
        @"#EXT-X-MEDIA-SEQUENCE:(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }
    public bool IsRunning => _listener is { IsListening: true };
    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public string? Bearer { get; set; }
    public byte[]? DefaultKeyBytes { get; set; }
    public RadioProxyCatalog? RadioCatalog { get; set; }
    public Func<IReadOnlyList<StreamProxyCatalogItem>>? GetStreamCatalog { get; set; }

    public async Task<(bool Ok, string Message)> StartAsync(int port)
    {
        if (IsRunning)
            return (false, "Stream server is already running.");

        await LiveStreamCredentials.ReloadAsync(this);

        try
        {
            Port = port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _listener = null;
            return (false, $"Could not bind port {port}: {ex.Message}");
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
        Log.Information("Stream proxy server started at {Url}", BaseUrl);
        return (true, BaseUrl);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener?.Close();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        Log.Information("Stream proxy server stopped");
    }

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequestAsync(ctx), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;

            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (path == "/" || path.Equals("/player", StringComparison.OrdinalIgnoreCase))
            {
                await ServePlayerPageAsync(ctx);
                return;
            }

            if (path.Equals("/api/channels", StringComparison.OrdinalIgnoreCase))
            {
                await ServeJsonAsync(ctx, BuildChannelPayload());
                return;
            }

            if (path.StartsWith("/api/channels/", StringComparison.OrdinalIgnoreCase))
            {
                var slotId = path["/api/channels/".Length..].Trim('/');
                if (string.IsNullOrWhiteSpace(slotId))
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                var slot = RadioCatalog?.GetSlot(slotId);
                if (slot == null)
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                await ServeJsonAsync(ctx, RadioProxyChannelInfo.FromSlot(slot, BaseUrl));
                return;
            }

            if (path.Equals("/api/streams", StringComparison.OrdinalIgnoreCase))
            {
                await ServeJsonAsync(ctx, BuildChannelPayload());
                return;
            }

            if (path.Equals("/primary.m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var primary = RadioCatalog?.GetDefaultSlot();
                if (primary == null || !primary.CanStream)
                {
                    await ServeTextAsync(ctx, (int)HttpStatusCode.ServiceUnavailable,
                        "No live channel available.");
                    return;
                }

                await ServeSlotPlaylistAsync(ctx, primary);
                return;
            }

            if (path.StartsWith("/radio/", StringComparison.OrdinalIgnoreCase))
            {
                await ServeRadioRouteAsync(ctx, path);
                return;
            }

            if (path.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
            {
                await ServeIndexedStreamAsync(ctx, path);
                return;
            }

            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Stream proxy request failed");
            ctx.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* ignore */ }
        }
    }

    private async Task ServeRadioRouteAsync(HttpListenerContext ctx, string path)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[0].Equals("radio", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        var slotId = parts[1];
        var resource = parts[2];
        var slot = RadioCatalog?.GetSlot(slotId);
        if (slot == null || !slot.Exposed)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        if (resource.Equals("stream.m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var nestedSrc = ctx.Request.QueryString["src"];
            if (!string.IsNullOrWhiteSpace(nestedSrc))
            {
                if (string.IsNullOrWhiteSpace(Bearer))
                {
                    await ServeTextAsync(ctx, (int)HttpStatusCode.ServiceUnavailable,
                        "Missing authorization.");
                    return;
                }

                var nested = await FetchUpstreamAsync(nestedSrc, requiresAuth: true);
                var pruned = PruneHlsTail(nested, MaxHlsSegments);
                var rewritten = RewriteHlsManifest(pruned, nestedSrc, slot.SlotId);
                await WriteM3u8Async(ctx, rewritten);
                return;
            }

            if (!slot.CanStream)
            {
                await ServeTextAsync(ctx, (int)HttpStatusCode.ServiceUnavailable,
                    slot.LastError ?? "Channel is not ready.");
                return;
            }

            await ServeSlotPlaylistAsync(ctx, slot);
            return;
        }

        if (resource.Equals("drm.key", StringComparison.OrdinalIgnoreCase))
        {
            await ServeSlotKeyAsync(ctx, slot);
            return;
        }

        if (resource.Equals("part", StringComparison.OrdinalIgnoreCase))
        {
            var upstream = ctx.Request.QueryString["src"];
            if (string.IsNullOrWhiteSpace(upstream))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            await ProxyMediaAsync(ctx, upstream, requiresAuth: true);
            return;
        }

        ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
    }

    private async Task ServeSlotPlaylistAsync(HttpListenerContext ctx, RadioProxySlot slot)
    {
        if (string.IsNullOrWhiteSpace(Bearer))
        {
            await ServeTextAsync(ctx, (int)HttpStatusCode.ServiceUnavailable,
                "Missing authorization. Play content in SRXMDL so credentials are captured.");
            return;
        }

        if (string.IsNullOrWhiteSpace(slot.SourcePlaylistUrl))
        {
            await ServeTextAsync(ctx, (int)HttpStatusCode.ServiceUnavailable, "Channel has no stream URL.");
            return;
        }

        var m3u8 = await FetchUpstreamAsync(slot.SourcePlaylistUrl, requiresAuth: true);
        var pruned = PruneHlsTail(m3u8, MaxHlsSegments);
        var rewritten = RewriteHlsManifest(pruned, slot.SourcePlaylistUrl, slot.SlotId);
        await WriteM3u8Async(ctx, rewritten);
    }

    private static async Task WriteM3u8Async(HttpListenerContext ctx, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        ctx.Response.StatusCode = (int)HttpStatusCode.OK;
        ctx.Response.ContentType = "application/vnd.apple.mpegurl";
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        ctx.Response.ContentLength64 = bytes.Length;
        if (!string.Equals(ctx.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private async Task ServeSlotKeyAsync(HttpListenerContext ctx, RadioProxySlot slot)
    {
        var key = slot.DrmKeyBytes ?? DefaultKeyBytes;
        if (key == null || key.Length == 0)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            return;
        }

        ctx.Response.StatusCode = (int)HttpStatusCode.OK;
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength64 = key.Length;
        if (!string.Equals(ctx.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            await ctx.Response.OutputStream.WriteAsync(key);
    }

    private object BuildChannelPayload()
    {
        var channels = RadioCatalog?.GetAllSlots()
            .Where(s => s.Exposed)
            .Select(s => RadioProxyChannelInfo.FromSlot(s, BaseUrl))
            .ToList() ?? [];

        return new
        {
            server = BaseUrl,
            channels,
            captures = GetStreamCatalog?.Invoke() ?? Array.Empty<StreamProxyCatalogItem>()
        };
    }

    private async Task ServeIndexedStreamAsync(HttpListenerContext ctx, string path)
    {
        var segment = path.Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (segment.Length != 2 || !int.TryParse(segment[1], out var index))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        var catalog = GetStreamCatalog?.Invoke() ?? [];
        var item = catalog.FirstOrDefault(i => i.Index == index);
        if (item == null || string.IsNullOrWhiteSpace(item.UpstreamUrl))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        if (item.StreamType.Equals("m3u8", StringComparison.OrdinalIgnoreCase) ||
            item.UpstreamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(Bearer))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                return;
            }

            var m3u8 = await FetchUpstreamAsync(item.UpstreamUrl, requiresAuth: true);
            var bytes = Encoding.UTF8.GetBytes(m3u8);
            ctx.Response.StatusCode = (int)HttpStatusCode.OK;
            ctx.Response.ContentType = "application/vnd.apple.mpegurl";
            ctx.Response.ContentLength64 = bytes.Length;
            if (!string.Equals(ctx.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
                await ctx.Response.OutputStream.WriteAsync(bytes);
            return;
        }

        await ProxyMediaAsync(ctx, item.UpstreamUrl, requiresAuth: false);
    }

    internal static string PruneHlsTail(string m3u8, int maxSegments)
    {
        if (maxSegments <= 0)
            return m3u8;

        var headerLines = new List<string>();
        var segmentEntries = new List<(List<string> Meta, string Url)>();
        var currentMeta = new List<string>();

        foreach (var rawLine in m3u8.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.StartsWith('#') && !string.IsNullOrWhiteSpace(line))
            {
                segmentEntries.Add((new List<string>(currentMeta), line.Trim()));
                currentMeta.Clear();
                continue;
            }

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-VERSION", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-TARGETDURATION", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-INDEPENDENT-SEGMENTS", StringComparison.OrdinalIgnoreCase))
            {
                headerLines.Add(line);
                continue;
            }

            if (line.StartsWith('#'))
                currentMeta.Add(line);
        }

        if (segmentEntries.Count <= maxSegments)
            return m3u8;

        var skipped = segmentEntries.Count - maxSegments;
        var tail = segmentEntries.Skip(skipped).ToList();

        var sb = new StringBuilder();
        var wroteSequence = false;
        foreach (var hdr in headerLines)
        {
            if (hdr.StartsWith("#EXT-X-MEDIA-SEQUENCE", StringComparison.OrdinalIgnoreCase))
            {
                var match = MediaSequenceRegex.Match(hdr);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var seq))
                {
                    sb.AppendLine($"#EXT-X-MEDIA-SEQUENCE:{seq + skipped}");
                    wroteSequence = true;
                    continue;
                }
            }

            sb.AppendLine(hdr);
        }

        if (!wroteSequence)
            sb.AppendLine($"#EXT-X-MEDIA-SEQUENCE:{skipped}");

        foreach (var (meta, url) in tail)
        {
            foreach (var metaLine in meta)
                sb.AppendLine(metaLine);
            sb.AppendLine(url);
        }

        return sb.ToString();
    }

    private async Task ServePlayerPageAsync(HttpListenerContext ctx)
    {
        var html = LoadEmbeddedPlayerHtml();
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode = (int)HttpStatusCode.OK;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        if (!string.Equals(ctx.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private static string LoadEmbeddedPlayerHtml()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("player.html", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            return "<html><body><p>Player page missing.</p></body></html>";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return "<html><body><p>Player page missing.</p></body></html>";

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private async Task ServeJsonAsync(HttpListenerContext ctx, object? payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = (int)HttpStatusCode.OK;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        if (!string.Equals(ctx.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private async Task ServeTextAsync(HttpListenerContext ctx, int statusCode, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        if (!string.Equals(ctx.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private async Task ProxyMediaAsync(HttpListenerContext ctx, string upstreamUrl, bool requiresAuth)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, upstreamUrl);
        if (requiresAuth && !string.IsNullOrWhiteSpace(Bearer))
            AddSxmHeaders(request);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        ctx.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType != null)
            ctx.Response.ContentType = response.Content.Headers.ContentType.ToString();

        if (response.Content.Headers.ContentLength is { } len)
            ctx.Response.ContentLength64 = len;

        if (!string.Equals(ctx.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase) &&
            response.IsSuccessStatusCode)
        {
            await response.Content.CopyToAsync(ctx.Response.OutputStream);
        }
    }

    private string RewriteHlsManifest(string m3u8, string playlistUrl, string slotId)
    {
        var playlistBase = new Uri(playlistUrl);
        var localKeyUrl = $"{BaseUrl}radio/{slotId}/drm.key";
        var sb = new StringBuilder();

        foreach (var rawLine in m3u8.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine(line);
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine(KeyUriRegex.Replace(line, $"$1{localKeyUrl}$2"));
                continue;
            }

            if (line.StartsWith('#'))
            {
                sb.AppendLine(RewriteTagLine(line, playlistBase, slotId));
                continue;
            }

            sb.AppendLine(RewriteMediaLine(line, playlistBase, slotId));
        }

        return sb.ToString();
    }

    private string RewriteTagLine(string line, Uri playlistBase, string slotId)
    {
        return TagUriRegex.Replace(line, match =>
        {
            var uriValue = match.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(uriValue))
                return match.Value;

            var absolute = ResolveToAbsolute(uriValue, playlistBase);
            if (IsM3u8(absolute))
                return $"{match.Groups[1].Value}{LocalVariantUrl(absolute, slotId)}{match.Groups[3].Value}";

            return $"{match.Groups[1].Value}{LocalPartUrl(absolute, slotId)}{match.Groups[3].Value}";
        });
    }

    private string RewriteMediaLine(string line, Uri playlistBase, string slotId)
    {
        var trimmed = line.Trim();
        var absolute = ResolveToAbsolute(trimmed, playlistBase);
        return IsM3u8(absolute) ? LocalVariantUrl(absolute, slotId) : LocalPartUrl(absolute, slotId);
    }

    private string LocalVariantUrl(string absoluteM3u8Url, string slotId) =>
        $"{BaseUrl}radio/{slotId}/stream.m3u8?src={Uri.EscapeDataString(absoluteM3u8Url)}";

    private string LocalPartUrl(string absoluteUrl, string slotId) =>
        $"{BaseUrl}radio/{slotId}/part?src={Uri.EscapeDataString(absoluteUrl)}";

    private static bool IsM3u8(string url) =>
        url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static string ResolveToAbsolute(string reference, Uri playlistBase)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        return new Uri(playlistBase, reference).ToString();
    }

    private async Task<string> FetchUpstreamAsync(string url, bool requiresAuth)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (requiresAuth)
            AddSxmHeaders(request);

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private void AddSxmHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(Bearer))
            request.Headers.TryAddWithoutValidation("Authorization", Bearer);

        request.Headers.TryAddWithoutValidation("Origin", "https://www.siriusxm.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.siriusxm.com");
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public static bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
