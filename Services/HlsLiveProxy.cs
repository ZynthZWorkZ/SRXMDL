using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SRXMDL.Services;

public sealed class HlsLiveProxy : IDisposable
{
    private static readonly Regex KeyUriRegex = new(
        @"(#EXT-X-KEY:[^\n]*URI="")[^""]*("")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagUriRegex = new(
        @"(URI="")([^""]*)("")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient = new();
    private readonly byte[] _keyBytes;
    private readonly string _bearer;
    private readonly string _upstreamM3u8Url;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;

    public HlsLiveProxy(string upstreamM3u8Url, string bearer, byte[] keyBytes)
    {
        _upstreamM3u8Url = upstreamM3u8Url;
        _bearer = bearer;
        _keyBytes = keyBytes;
    }

    public string PlaylistUrl =>
        $"http://127.0.0.1:{_port}/playlist.m3u8?u={Uri.EscapeDataString(_upstreamM3u8Url)}";

    public void Start()
    {
        _port = GetFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener?.Close();
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
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals("/key", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength64 = _keyBytes.Length;
                await ctx.Response.OutputStream.WriteAsync(_keyBytes);
            }
            else if (path.Equals("/playlist.m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var upstreamUrl = ctx.Request.QueryString["u"];
                if (string.IsNullOrWhiteSpace(upstreamUrl))
                    upstreamUrl = _upstreamM3u8Url;

                var m3u8 = await FetchUpstreamPlaylistAsync(upstreamUrl);
                var patched = PatchPlaylist(m3u8, upstreamUrl);
                var bytes = Encoding.UTF8.GetBytes(patched);
                ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            }
        }
        catch (Exception)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* ignore */ }
        }
    }

    private string PatchPlaylist(string m3u8, string playlistUrl)
    {
        var playlistBase = new Uri(playlistUrl);
        var localKeyUrl = $"http://127.0.0.1:{_port}/key";
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
                sb.AppendLine(PatchTagLine(line, playlistBase));
                continue;
            }

            sb.AppendLine(PatchMediaLine(line, playlistBase));
        }

        return sb.ToString();
    }

    private string PatchTagLine(string line, Uri playlistBase)
    {
        return TagUriRegex.Replace(line, match =>
        {
            var uriValue = match.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(uriValue))
                return match.Value;

            var absolute = ResolveToAbsolute(uriValue, playlistBase);
            if (IsM3u8(absolute))
            {
                var proxied = ProxyPlaylistUrl(absolute);
                return $"{match.Groups[1].Value}{proxied}{match.Groups[3].Value}";
            }

            return $"{match.Groups[1].Value}{absolute}{match.Groups[3].Value}";
        });
    }

    private string PatchMediaLine(string line, Uri playlistBase)
    {
        var trimmed = line.Trim();
        var absolute = ResolveToAbsolute(trimmed, playlistBase);
        return IsM3u8(absolute) ? ProxyPlaylistUrl(absolute) : absolute;
    }

    private string ProxyPlaylistUrl(string absoluteM3u8Url) =>
        $"http://127.0.0.1:{_port}/playlist.m3u8?u={Uri.EscapeDataString(absoluteM3u8Url)}";

    private static bool IsM3u8(string url) =>
        url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static string ResolveToAbsolute(string reference, Uri playlistBase)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        return new Uri(playlistBase, reference).ToString();
    }

    private async Task<string> FetchUpstreamPlaylistAsync(string playlistUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, playlistUrl);
        request.Headers.TryAddWithoutValidation("Authorization", _bearer);
        request.Headers.TryAddWithoutValidation("Origin", "https://www.siriusxm.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.siriusxm.com");
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
