using System.Collections.Concurrent;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Serilog;
using SRXMDL.Models;

namespace SRXMDL.Services;

/// <summary>
/// "Super Log" — an exhaustive CDP network recorder used purely for debugging/reverse
/// engineering the SiriusXM web player. Unlike <see cref="WebViewNetworkMonitor"/> (which only
/// cares about media stream URLs), this captures every request/response pair with full
/// headers, cookies, CORS/blocking diagnostics, textual response bodies, and websocket traffic,
/// then dumps it as pretty-printed JSON so it can be reviewed later to find new metadata
/// endpoints (now playing / up next / station info / keys) or spot changes in the web app.
/// </summary>
public sealed class SuperNetworkLogger
{
    private static readonly HashSet<string> TextualMimeTypePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json",
        "application/javascript",
        "application/x-javascript",
        "application/xml",
        "application/manifest",
        "application/vnd.apple.mpegurl",
        "application/x-mpegurl",
        "text/"
    };

    private const int MaxBodyLength = 150_000;

    private readonly ConcurrentDictionary<string, SuperLogEntry> _entries = new();
    private readonly ConcurrentDictionary<string, SuperLogWebSocketEntry> _webSockets = new();
    private readonly List<CoreWebView2DevToolsProtocolEventReceiver> _receivers = new();
    private readonly List<(CoreWebView2DevToolsProtocolEventReceiver Receiver, EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> Handler)> _subscriptions = new();

    private CoreWebView2? _core;
    private DateTimeOffset _startedAt;
    private int _entryCount;

    public bool IsRecording { get; private set; }

    public event Action<int>? EntryCountChanged;

    public async Task StartAsync(CoreWebView2 core)
    {
        if (IsRecording)
            return;

        _entries.Clear();
        _webSockets.Clear();
        _entryCount = 0;
        _core = core;
        _startedAt = DateTimeOffset.Now;

        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");

        Subscribe("Network.requestWillBeSent", OnRequestWillBeSent);
        Subscribe("Network.requestWillBeSentExtraInfo", OnRequestWillBeSentExtraInfo);
        Subscribe("Network.responseReceived", OnResponseReceived);
        Subscribe("Network.responseReceivedExtraInfo", OnResponseReceivedExtraInfo);
        Subscribe("Network.loadingFinished", OnLoadingFinished);
        Subscribe("Network.loadingFailed", OnLoadingFailed);
        Subscribe("Network.webSocketCreated", OnWebSocketCreated);
        Subscribe("Network.webSocketClosed", OnWebSocketClosed);
        Subscribe("Network.webSocketFrameSent", OnWebSocketFrameSent);
        Subscribe("Network.webSocketFrameReceived", OnWebSocketFrameReceived);

        IsRecording = true;
        Log.Information("Super Log started — capturing full network traffic for debugging");
    }

    public async Task<string> StopAndSaveAsync()
    {
        UnsubscribeAll();
        IsRecording = false;

        var document = new SuperLogDocument
        {
            GeneratedAt = DateTimeOffset.Now,
            StartedAt = _startedAt,
            DurationSeconds = (DateTimeOffset.Now - _startedAt).TotalSeconds,
            HttpRequests = _entries.Values.OrderBy(x => x.RequestTimestamp).ToList(),
            WebSockets = _webSockets.Values.OrderBy(x => x.CreatedAt).ToList()
        };
        document.TotalHttpRequests = document.HttpRequests.Count;
        document.TotalWebSockets = document.WebSockets.Count;

        var folder = Path.Combine(Path.GetTempPath(), "SRXMDL_SuperLogs");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"superlog_{DateTime.Now:yyyyMMdd_HHmmss}.json");

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, options));
        Log.Information("Super Log saved to {Path} ({Count} requests, {Sockets} websockets)",
            path, document.TotalHttpRequests, document.TotalWebSockets);

        _entries.Clear();
        _webSockets.Clear();
        _core = null;

        return path;
    }

    private void Subscribe(string eventName, EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> handler)
    {
        if (_core == null)
            return;

        var receiver = _core.GetDevToolsProtocolEventReceiver(eventName);
        receiver.DevToolsProtocolEventReceived += handler;
        _subscriptions.Add((receiver, handler));
    }

    private void UnsubscribeAll()
    {
        foreach (var (receiver, handler) in _subscriptions)
            receiver.DevToolsProtocolEventReceived -= handler;

        _subscriptions.Clear();
    }

    private void RaiseCountChanged()
    {
        var count = Interlocked.Increment(ref _entryCount);
        EntryCountChanged?.Invoke(count);
    }

    private SuperLogEntry GetOrAddEntry(string requestId) =>
        _entries.GetOrAdd(requestId, id => new SuperLogEntry { RequestId = id });

    private void OnRequestWillBeSent(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("requestId", out var idProp))
                return;
            var requestId = idProp.GetString();
            if (string.IsNullOrEmpty(requestId))
                return;

            var entry = GetOrAddEntry(requestId);
            entry.RequestTimestamp = DateTimeOffset.Now;

            if (root.TryGetProperty("type", out var typeProp))
                entry.ResourceType = typeProp.GetString() ?? string.Empty;

            if (root.TryGetProperty("initiator", out var initiatorProp))
                entry.Initiator = initiatorProp.GetRawText();

            if (root.TryGetProperty("request", out var request))
            {
                if (request.TryGetProperty("url", out var urlProp))
                    entry.Url = urlProp.GetString() ?? string.Empty;

                if (request.TryGetProperty("method", out var methodProp))
                    entry.Method = methodProp.GetString() ?? string.Empty;

                if (request.TryGetProperty("postData", out var postDataProp))
                    entry.RequestPostData = postDataProp.GetString();

                if (request.TryGetProperty("headers", out var headersProp))
                {
                    foreach (var header in headersProp.EnumerateObject())
                        entry.RequestHeaders[header.Name] = header.Value.GetString() ?? string.Empty;
                }
            }

            RaiseCountChanged();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SuperLog: error parsing requestWillBeSent");
        }
    }

    private void OnRequestWillBeSentExtraInfo(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("requestId", out var idProp))
                return;
            var requestId = idProp.GetString();
            if (string.IsNullOrEmpty(requestId))
                return;

            var entry = GetOrAddEntry(requestId);

            if (root.TryGetProperty("headers", out var headersProp))
            {
                foreach (var header in headersProp.EnumerateObject())
                    entry.RequestHeaders[header.Name] = header.Value.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("associatedCookies", out var cookiesProp) && cookiesProp.ValueKind == JsonValueKind.Array)
            {
                var cookies = new List<string>();
                foreach (var cookieEntry in cookiesProp.EnumerateArray())
                {
                    if (cookieEntry.TryGetProperty("cookie", out var cookie) &&
                        cookie.TryGetProperty("name", out var nameProp))
                    {
                        var value = cookie.TryGetProperty("value", out var valueProp) ? valueProp.GetString() : null;
                        cookies.Add($"{nameProp.GetString()}={value}");
                    }
                }
                if (cookies.Count > 0)
                    entry.RequestCookies = cookies;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SuperLog: error parsing requestWillBeSentExtraInfo");
        }
    }

    private void OnResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("requestId", out var idProp))
                return;
            var requestId = idProp.GetString();
            if (string.IsNullOrEmpty(requestId))
                return;

            if (!root.TryGetProperty("response", out var response))
                return;

            var entry = GetOrAddEntry(requestId);
            entry.ResponseTimestamp = DateTimeOffset.Now;

            if (response.TryGetProperty("url", out var urlProp) && string.IsNullOrEmpty(entry.Url))
                entry.Url = urlProp.GetString() ?? string.Empty;

            if (response.TryGetProperty("status", out var statusProp))
                entry.ResponseStatus = statusProp.GetInt32();

            if (response.TryGetProperty("statusText", out var statusTextProp))
                entry.ResponseStatusText = statusTextProp.GetString();

            if (response.TryGetProperty("mimeType", out var mimeProp))
                entry.ResponseMimeType = mimeProp.GetString();

            if (response.TryGetProperty("fromDiskCache", out var diskCacheProp))
                entry.FromDiskCache = diskCacheProp.GetBoolean();

            if (response.TryGetProperty("fromServiceWorker", out var serviceWorkerProp))
                entry.FromServiceWorker = serviceWorkerProp.GetBoolean();

            if (response.TryGetProperty("headers", out var headersProp))
            {
                entry.ResponseHeaders ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in headersProp.EnumerateObject())
                    entry.ResponseHeaders[header.Name] = header.Value.GetString() ?? string.Empty;
            }

            RaiseCountChanged();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SuperLog: error parsing responseReceived");
        }
    }

    private void OnResponseReceivedExtraInfo(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("requestId", out var idProp))
                return;
            var requestId = idProp.GetString();
            if (string.IsNullOrEmpty(requestId))
                return;

            var entry = GetOrAddEntry(requestId);

            if (root.TryGetProperty("headers", out var headersProp))
            {
                entry.ResponseHeaders ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in headersProp.EnumerateObject())
                    entry.ResponseHeaders[header.Name] = header.Value.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("corsErrorStatus", out var corsProp))
                entry.CorsErrorStatus = corsProp.GetRawText();

            if (root.TryGetProperty("blockedCookies", out var blockedProp) && blockedProp.ValueKind == JsonValueKind.Array)
            {
                var blocked = new List<string>();
                foreach (var b in blockedProp.EnumerateArray())
                {
                    if (b.TryGetProperty("cookie", out var cookie) && cookie.TryGetProperty("name", out var nameProp))
                        blocked.Add(nameProp.GetString() ?? string.Empty);
                }
                if (blocked.Count > 0)
                    entry.BlockedCookies = blocked;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SuperLog: error parsing responseReceivedExtraInfo");
        }
    }

    private async void OnLoadingFinished(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("requestId", out var idProp))
                return;
            var requestId = idProp.GetString();
            if (string.IsNullOrEmpty(requestId) || !_entries.TryGetValue(requestId, out var entry))
                return;

            if (root.TryGetProperty("encodedDataLength", out var lengthProp))
                entry.EncodedDataLength = (long)lengthProp.GetDouble();

            if (IsTextualMimeType(entry.ResponseMimeType))
                await TryFetchBodyAsync(requestId, entry);
            else
            {
                entry.BodyOmitted = true;
                entry.BodyOmittedReason = $"Skipped non-textual content (mimeType: {entry.ResponseMimeType ?? "unknown"})";
            }

            RaiseCountChanged();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SuperLog: error parsing loadingFinished");
        }
    }

    private void OnLoadingFailed(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("requestId", out var idProp))
                return;
            var requestId = idProp.GetString();
            if (string.IsNullOrEmpty(requestId))
                return;

            var entry = GetOrAddEntry(requestId);
            entry.Failed = true;

            if (root.TryGetProperty("errorText", out var errorProp))
                entry.ErrorText = errorProp.GetString();

            if (root.TryGetProperty("canceled", out var canceledProp))
                entry.Canceled = canceledProp.GetBoolean();

            if (root.TryGetProperty("blockedReason", out var blockedReasonProp))
                entry.BlockedReason = blockedReasonProp.GetString();

            if (root.TryGetProperty("corsErrorStatus", out var corsProp))
                entry.CorsErrorStatus = corsProp.GetRawText();

            RaiseCountChanged();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SuperLog: error parsing loadingFailed");
        }
    }

    private async Task TryFetchBodyAsync(string requestId, SuperLogEntry entry)
    {
        if (_core == null)
            return;

        try
        {
            var resultJson = await _core.CallDevToolsProtocolMethodAsync(
                "Network.getResponseBody",
                JsonSerializer.Serialize(new { requestId }));

            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;

            var base64Encoded = root.TryGetProperty("base64Encoded", out var b64Prop) && b64Prop.GetBoolean();
            var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;

            if (body == null)
                return;

            if (body.Length > MaxBodyLength)
            {
                entry.ResponseBody = body[..MaxBodyLength];
                entry.BodyTruncated = true;
            }
            else
            {
                entry.ResponseBody = body;
            }

            entry.ResponseBodyBase64Encoded = base64Encoded;
        }
        catch (Exception ex)
        {
            entry.BodyOmitted = true;
            entry.BodyOmittedReason = $"Body unavailable: {ex.Message}";
            Log.Debug(ex, "SuperLog: could not fetch response body for {RequestId}", requestId);
        }
    }

    private static bool IsTextualMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return false;

        return TextualMimeTypePrefixes.Any(prefix =>
            mimeType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private void OnWebSocketCreated(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("requestId", out var idProp))
                return;
            var requestId = idProp.GetString();
            if (string.IsNullOrEmpty(requestId))
                return;

            var url = root.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? string.Empty : string.Empty;

            _webSockets[requestId] = new SuperLogWebSocketEntry
            {
                RequestId = requestId,
                Url = url,
                CreatedAt = DateTimeOffset.Now
            };

            RaiseCountChanged();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SuperLog: error parsing webSocketCreated");
        }
    }

    private void OnWebSocketClosed(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("requestId", out var idProp))
                return;
            var requestId = idProp.GetString();
            if (string.IsNullOrEmpty(requestId) || !_webSockets.TryGetValue(requestId, out var socket))
                return;

            socket.ClosedAt = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SuperLog: error parsing webSocketClosed");
        }
    }

    private void OnWebSocketFrameSent(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e) =>
        RecordWebSocketFrame(e, "sent");

    private void OnWebSocketFrameReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e) =>
        RecordWebSocketFrame(e, "received");

    private void RecordWebSocketFrame(CoreWebView2DevToolsProtocolEventReceivedEventArgs e, string direction)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("requestId", out var idProp))
                return;
            var requestId = idProp.GetString();
            if (string.IsNullOrEmpty(requestId))
                return;

            var socket = _webSockets.GetOrAdd(requestId, id => new SuperLogWebSocketEntry
            {
                RequestId = id,
                CreatedAt = DateTimeOffset.Now
            });

            if (!root.TryGetProperty("response", out var response))
                return;

            var frame = new SuperLogWebSocketFrame
            {
                Direction = direction,
                Timestamp = DateTimeOffset.Now
            };

            if (response.TryGetProperty("opcode", out var opcodeProp))
                frame.Opcode = opcodeProp.GetInt32();

            if (response.TryGetProperty("mask", out var maskProp))
                frame.Mask = maskProp.GetBoolean();

            if (response.TryGetProperty("payloadData", out var payloadProp))
            {
                var payload = payloadProp.GetString() ?? string.Empty;
                frame.PayloadData = payload.Length > MaxBodyLength ? payload[..MaxBodyLength] : payload;
            }

            socket.Frames.Add(frame);
            RaiseCountChanged();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SuperLog: error parsing websocket frame ({Direction})", direction);
        }
    }
}
