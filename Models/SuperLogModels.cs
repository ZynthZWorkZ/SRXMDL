namespace SRXMDL.Models;

/// <summary>
/// A single HTTP(S) request/response pair captured via CDP Network events, with as much
/// detail as the protocol exposes: headers, cookies, CORS/blocking info, and (for textual
/// content) the response body. Intended for offline analysis of the SiriusXM web player to
/// reverse engineer metadata endpoints (now playing, up next, station info, DRM/keys, etc).
/// </summary>
public sealed class SuperLogEntry
{
    public string RequestId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string? Initiator { get; set; }

    public DateTimeOffset RequestTimestamp { get; set; }
    public Dictionary<string, string> RequestHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? RequestPostData { get; set; }
    public List<string>? RequestCookies { get; set; }

    public DateTimeOffset? ResponseTimestamp { get; set; }
    public int? ResponseStatus { get; set; }
    public string? ResponseStatusText { get; set; }
    public string? ResponseMimeType { get; set; }
    public Dictionary<string, string>? ResponseHeaders { get; set; }
    public bool FromDiskCache { get; set; }
    public bool FromServiceWorker { get; set; }

    public string? ResponseBody { get; set; }
    public bool ResponseBodyBase64Encoded { get; set; }
    public bool BodyOmitted { get; set; }
    public string? BodyOmittedReason { get; set; }
    public bool BodyTruncated { get; set; }
    public long? EncodedDataLength { get; set; }

    public bool Failed { get; set; }
    public string? ErrorText { get; set; }
    public bool Canceled { get; set; }
    public string? BlockedReason { get; set; }
    public string? CorsErrorStatus { get; set; }
    public List<string>? BlockedCookies { get; set; }
}

public sealed class SuperLogWebSocketFrame
{
    public string Direction { get; set; } = string.Empty; // "sent" or "received"
    public DateTimeOffset Timestamp { get; set; }
    public int? Opcode { get; set; }
    public bool Mask { get; set; }
    public string? PayloadData { get; set; }
}

public sealed class SuperLogWebSocketEntry
{
    public string RequestId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public List<SuperLogWebSocketFrame> Frames { get; set; } = new();
}

public sealed class SuperLogDocument
{
    public string Notes { get; set; } =
        "Full CDP network capture (requests, responses, headers, cookies, CORS/blocking info, bodies, websockets) " +
        "for the SiriusXM web player. Use this to find metadata endpoints (now playing / up next / station info) " +
        "and track changes to the web app over time.";
    public DateTimeOffset GeneratedAt { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public double DurationSeconds { get; set; }
    public int TotalHttpRequests { get; set; }
    public int TotalWebSockets { get; set; }
    public List<SuperLogEntry> HttpRequests { get; set; } = new();
    public List<SuperLogWebSocketEntry> WebSockets { get; set; } = new();
}
