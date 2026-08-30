using System.Text.Json;
using SRXMDL.Models;

namespace SRXMDL.Services;

public sealed class RadioChannelDetails
{
    public string? ChannelLogoUrl { get; init; }
    public string? ChannelTileUrl { get; init; }
    public string? ChannelBackgroundUrl { get; init; }
    public int? ChannelNumberCanonical { get; init; }
    public string? ShowImageUrl { get; init; }
}

public static class LiveChannelMetadataExtractor
{
    public static RadioChannelDetails FromTuneSource(JsonElement root)
    {
        var live = default(JsonElement);
        var hasLive = root.TryGetProperty("metadata", out var metadata) &&
                      metadata.TryGetProperty("live", out live);

        int? canonical = null;
        if (hasLive &&
            live.TryGetProperty("channelNumberCanonical", out var canonicalProp) &&
            canonicalProp.TryGetInt32(out var canonicalNumber))
        {
            canonical = canonicalNumber;
        }

        return new RadioChannelDetails
        {
            ChannelLogoUrl = ExtractRootImage(root, "logo", "aspect_1x1") ??
                             ExtractRootImage(root, "logo", "aspect_5x4") ??
                             ExtractRootImage(root, "white_logo", "aspect_5x4") ??
                             ExtractRootImage(root, "white_logo"),
            ChannelTileUrl = ExtractRootImage(root, "tile"),
            ChannelBackgroundUrl = ExtractRootImage(root, "tile_background") ?? ExtractRootImage(root, "hero_tile"),
            ChannelNumberCanonical = canonical,
            ShowImageUrl = hasLive ? ResolveCurrentLiveShowImage(live) : null
        };
    }

    public static (string? ShowName, string? ShowImageUrl, bool IsPlayByPlay) FromLookAroundChannel(JsonElement channelElement)
    {
        var isPlayByPlay = channelElement.TryGetProperty("pxp", out var pxpProp) &&
                           pxpProp.ValueKind == JsonValueKind.True;

        if (!channelElement.TryGetProperty("shows", out var shows) || shows.ValueKind != JsonValueKind.Array)
            return (null, null, isPlayByPlay);

        string? bestName = null;
        string? bestImage = null;
        DateTime bestTime = DateTime.MinValue;

        foreach (var show in shows.EnumerateArray())
        {
            var name = GetString(show, "name");
            var validFrom = ParseUtc(GetString(show, "validFrom")) ?? DateTime.MinValue;
            var imageUrl = ExtractLookAroundImage(show);

            if (validFrom >= bestTime)
            {
                bestTime = validFrom;
                bestName = name ?? bestName;
                bestImage = imageUrl ?? bestImage;
            }
            else if (bestName == null && !string.IsNullOrWhiteSpace(name))
            {
                bestName = name;
                bestImage ??= imageUrl;
            }
        }

        return (bestName, bestImage, isPlayByPlay);
    }

    private static string? ResolveCurrentLiveShowImage(JsonElement live)
    {
        if (!live.TryGetProperty("episodes", out var episodes) || episodes.ValueKind != JsonValueKind.Array)
            return null;

        var now = DateTime.UtcNow;
        string? fallback = null;

        foreach (var episode in episodes.EnumerateArray())
        {
            var image = ExtractEpisodeShowImage(episode);
            if (string.IsNullOrWhiteSpace(image))
                continue;

            fallback ??= image;

            if (!episode.TryGetProperty("startTimestamp", out var startProp) ||
                startProp.ValueKind != JsonValueKind.String ||
                !DateTime.TryParse(startProp.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var startUtc))
            {
                continue;
            }

            var durationMs = GetInt64(episode, "duration") ?? 0;
            var endUtc = durationMs > 0 ? startUtc.AddMilliseconds(durationMs) : startUtc.AddHours(5);
            if (startUtc <= now && now < endUtc)
                return image;
        }

        return fallback;
    }

    private static string? ExtractEpisodeShowImage(JsonElement episode)
    {
        if (episode.TryGetProperty("showImages", out var showImages))
            return ExtractImageGroup(showImages, "tile") ?? ExtractImageGroup(showImages, "hero_tile");

        return ExtractImageGroup(episode, "images");
    }

    private static string? ExtractRootImage(JsonElement root, string groupName, string aspect = "aspect_1x1")
    {
        if (!root.TryGetProperty("images", out var images) ||
            !images.TryGetProperty(groupName, out var group))
        {
            return null;
        }

        return ExtractImageGroup(group, aspect: aspect);
    }

    private static string? ExtractImageGroup(JsonElement group, string aspect = "aspect_1x1")
    {
        if (!group.TryGetProperty(aspect, out var aspectElement))
        {
            if (aspect != "aspect_1x1" && group.TryGetProperty("aspect_1x1", out aspectElement))
            {
                // fallback aspect
            }
            else if (group.TryGetProperty("aspect_16x9", out aspectElement))
            {
                // wide fallback
            }
            else
            {
                return null;
            }
        }

        if (aspectElement.TryGetProperty("preferredImage", out var preferred) &&
            preferred.TryGetProperty("url", out var preferredUrl))
        {
            return StreamEntry.DecodeImageUrl(preferredUrl.GetString());
        }

        if (aspectElement.TryGetProperty("defaultImage", out var defaultImage) &&
            defaultImage.TryGetProperty("url", out var defaultUrl))
        {
            return StreamEntry.DecodeImageUrl(defaultUrl.GetString());
        }

        return null;
    }

    private static string? ExtractLookAroundImage(JsonElement element)
    {
        if (!element.TryGetProperty("image", out var image) ||
            !image.TryGetProperty("url", out var urlProp))
        {
            return null;
        }

        return StreamEntry.DecodeImageUrl(urlProp.GetString());
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) ? prop.GetString() : null;

    private static long? GetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind == JsonValueKind.Number &&
        prop.TryGetInt64(out var value)
            ? value
            : null;

    private static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
}
