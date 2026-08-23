using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Serilog;
using SRXMDL.Models;

namespace SRXMDL.Services;

public sealed class WebViewNetworkMonitor
{
    private readonly IStreamCaptureHost _host;
    private readonly StreamNetworkProcessor _processor;
    private readonly ConcurrentDictionary<string, NetworkRequestInfo> _pendingRequests = new();

    private CoreWebView2? _core;
    private CoreWebView2DevToolsProtocolEventReceiver? _requestReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _responseReceiver;
    private bool _started;

    public WebViewNetworkMonitor(IStreamCaptureHost host, StreamNetworkProcessor processor)
    {
        _host = host;
        _processor = processor;
    }

    public async Task StartAsync(CoreWebView2 core)
    {
        if (_started && _core == core)
            return;

        Stop();

        _core = core;
        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");

        _requestReceiver = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
        _requestReceiver.DevToolsProtocolEventReceived += OnRequestWillBeSent;

        _responseReceiver = core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
        _responseReceiver.DevToolsProtocolEventReceived += OnResponseReceived;

        _started = true;
        Log.Information("WebView2 CDP network monitoring started");
    }

    public void Stop()
    {
        if (_requestReceiver != null)
            _requestReceiver.DevToolsProtocolEventReceived -= OnRequestWillBeSent;

        if (_responseReceiver != null)
            _responseReceiver.DevToolsProtocolEventReceived -= OnResponseReceived;

        _requestReceiver = null;
        _responseReceiver = null;
        _core = null;
        _started = false;
        _pendingRequests.Clear();
    }

    private void OnRequestWillBeSent(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("requestId", out var requestIdProp))
                return;

            var requestId = requestIdProp.GetString();
            if (string.IsNullOrEmpty(requestId))
                return;

            if (!root.TryGetProperty("request", out var request))
                return;

            var url = request.TryGetProperty("url", out var urlProp)
                ? urlProp.GetString() ?? string.Empty
                : string.Empty;

            string? postData = null;
            if (request.TryGetProperty("postData", out var postDataProp))
                postData = postDataProp.GetString();

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (request.TryGetProperty("headers", out var headersElement))
            {
                foreach (var header in headersElement.EnumerateObject())
                {
                    headers[header.Name] = header.Value.GetString() ?? string.Empty;
                }
            }

            _pendingRequests[requestId] = new NetworkRequestInfo
            {
                Url = url,
                PostData = postData,
                Headers = headers
            };

            Log.Debug("CDP request observed: {Url}", url);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error parsing Network.requestWillBeSent");
        }
    }

    private async void OnResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("requestId", out var requestIdProp))
                return;

            var requestId = requestIdProp.GetString();
            if (string.IsNullOrEmpty(requestId))
                return;

            if (!root.TryGetProperty("response", out var response))
                return;

            if (!response.TryGetProperty("url", out var urlProp))
                return;

            var url = urlProp.GetString();
            if (string.IsNullOrEmpty(url))
                return;

            _pendingRequests.TryRemove(requestId, out var requestInfo);
            await _processor.HandleResponseAsync(url, requestInfo, _host);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error handling Network.responseReceived");
        }
    }
}
