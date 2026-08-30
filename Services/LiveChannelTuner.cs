using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Serilog;

namespace SRXMDL.Services;

public sealed class LiveTuneResult
{
    public bool Ok { get; init; }
    public string? M3u8Url { get; init; }
    public string? ChannelName { get; init; }
    public int? ChannelNumber { get; init; }
    public RadioChannelDetails? Details { get; init; }
    public string? Error { get; init; }
}

public sealed class LiveChannelTuner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string? ExtractPrimaryM3u8(JsonElement root)
    {
        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var stream in streams.EnumerateArray())
        {
            if (!stream.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var urlEntry in urls.EnumerateArray())
            {
                if (!urlEntry.TryGetProperty("isPrimary", out var isPrimary) || !isPrimary.GetBoolean())
                    continue;

                if (!urlEntry.TryGetProperty("url", out var urlProp))
                    continue;

                var url = urlProp.GetString();
                if (!string.IsNullOrWhiteSpace(url) &&
                    url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) &&
                    !LiveRadioRecorder.IsVodM3u8(url))
                {
                    return url;
                }
            }
        }

        return null;
    }

    public static string BuildChannelLinearPayload(string channelId) =>
        JsonSerializer.Serialize(new { type = "channel-linear", id = channelId }, JsonOptions);

    public async Task<LiveTuneResult> TuneChannelAsync(
        string channelId,
        string tuneSourceUrl,
        string? payloadTemplate,
        string authToken)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            return new LiveTuneResult { Ok = false, Error = "Channel id is required." };

        if (string.IsNullOrWhiteSpace(tuneSourceUrl) || string.IsNullOrWhiteSpace(authToken))
            return new LiveTuneResult { Ok = false, Error = "Missing tuneSource URL or authorization. Play live radio in SRXMDL first." };

        var payload = BuildPayload(channelId, payloadTemplate);

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authToken);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.siriusxm.com");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.siriusxm.com");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(tuneSourceUrl, content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("tuneSource failed for {ChannelId}: {Status}", channelId, response.StatusCode);
                return new LiveTuneResult { Ok = false, Error = $"tuneSource returned {(int)response.StatusCode}" };
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var m3u8 = ExtractPrimaryM3u8(root);
            if (string.IsNullOrWhiteSpace(m3u8))
                return new LiveTuneResult { Ok = false, Error = "No live m3u8 URL in tuneSource response." };

            var (channelName, channelNumber) = ExtractLiveChannelIdentity(root);
            Log.Information("Tuned channel {ChannelId} -> {Url}", channelId, m3u8);
            return new LiveTuneResult
            {
                Ok = true,
                M3u8Url = m3u8,
                ChannelName = channelName,
                ChannelNumber = channelNumber,
                Details = LiveChannelMetadataExtractor.FromTuneSource(root)
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to tune channel {ChannelId}", channelId);
            return new LiveTuneResult { Ok = false, Error = ex.Message };
        }
    }

    public static (string? ChannelName, int? ChannelNumber) ExtractLiveChannelIdentity(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("live", out var live))
        {
            return (null, null);
        }

        var channelName = live.TryGetProperty("channelName", out var nameProp) ? nameProp.GetString() : null;
        int? channelNumber = live.TryGetProperty("channelNumber", out var numberProp) &&
                             numberProp.TryGetInt32(out var number)
            ? number
            : null;

        return (channelName, channelNumber);
    }

    private static string BuildPayload(string channelId, string? payloadTemplate)
    {
        if (string.IsNullOrWhiteSpace(payloadTemplate))
            return BuildChannelLinearPayload(channelId);

        try
        {
            using var doc = JsonDocument.Parse(payloadTemplate);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("id"))
                        writer.WriteString("id", channelId);
                    else if (prop.NameEquals("type"))
                        writer.WriteString("type", "channel-linear");
                    else
                        prop.WriteTo(writer);
                }

                if (!doc.RootElement.TryGetProperty("type", out _))
                    writer.WriteString("type", "channel-linear");
                if (!doc.RootElement.TryGetProperty("id", out _))
                    writer.WriteString("id", channelId);

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return BuildChannelLinearPayload(channelId);
        }
    }
}
