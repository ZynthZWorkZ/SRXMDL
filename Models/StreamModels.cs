using System.Text.Json;
using Serilog;

namespace SRXMDL.Models;

public class StreamEntry
{
    public string Timestamp { get; set; } = string.Empty;
    public string StreamType { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string TrackName { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public string? PreferredImageUrl { get; set; }
    public bool CanPlay => StreamType is "mp4" or "mp3" or "m3u8";

    public static string? DecodeImageUrl(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        try
        {
            var jsonObject = new
            {
                key = imagePath,
                edits = new object[]
                {
                    new { format = new { type = "jpeg" } },
                    new { resize = new { width = 1080, height = 1080 } }
                }
            };
            var jsonString = JsonSerializer.Serialize(jsonObject);
            var base64String = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(jsonString));
            return "https://imgsrv-sxm-prod-device.streaming.siriusxm.com/" + base64String;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error decoding image URL for path: {Path}", imagePath);
            return null;
        }
    }
}

public class NowPlaying
{
    public string TrackName { get; set; } = "No track playing";
    public string StationName { get; set; } = "No station selected";
    public string? AlbumArtUrl { get; set; }
}

public sealed class NetworkRequestInfo
{
    public string Url { get; init; } = string.Empty;
    public string? PostData { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GetAuthorization() =>
        Headers.TryGetValue("Authorization", out var value) ? value : null;
}
