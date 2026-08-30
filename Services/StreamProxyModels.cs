namespace SRXMDL.Services;

public sealed class StreamProxyCatalogItem
{
    public int Index { get; init; }
    public string StreamType { get; init; } = string.Empty;
    public string TrackName { get; init; } = string.Empty;
    public string ArtistName { get; init; } = string.Empty;
    public string? AlbumName { get; init; }
    public string? ImageUrl { get; init; }
    public string UpstreamUrl { get; init; } = string.Empty;
    public string ProxyUrl { get; init; } = string.Empty;
}

public sealed class RadioProxyChannelInfo
{
    public string SlotId { get; init; } = string.Empty;
    public string ChannelId { get; init; } = string.Empty;
    public string ChannelName { get; init; } = string.Empty;
    public int? ChannelNumber { get; init; }
    public int? ChannelNumberCanonical { get; init; }
    public string? ShowName { get; init; }
    public string State { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
    public bool IsPlayByPlay { get; init; }
    public string StreamUrl { get; init; } = string.Empty;
    public string InfoUrl { get; init; } = string.Empty;
    public string? ChannelLogoUrl { get; init; }
    public string? ChannelTileUrl { get; init; }
    public string? ChannelBackgroundUrl { get; init; }
    public string? ShowImageUrl { get; init; }
    public string? DisplayImageUrl { get; init; }
    public string? TrackName { get; init; }
    public string? ArtistName { get; init; }
    public string? NowPlayingImageUrl { get; init; }
    public string? Error { get; init; }
    public DateTime UpdatedUtc { get; init; }

    public static RadioProxyChannelInfo FromSlot(RadioProxySlot slot, string baseUrl) => new()
    {
        SlotId = slot.SlotId,
        ChannelId = slot.ChannelId,
        ChannelName = slot.ChannelName,
        ChannelNumber = slot.ChannelNumber,
        ChannelNumberCanonical = slot.ChannelNumberCanonical,
        ShowName = slot.ShowName,
        State = slot.State.ToString(),
        IsDefault = slot.IsDefault,
        IsPlayByPlay = slot.IsPlayByPlay,
        StreamUrl = baseUrl.TrimEnd('/') + slot.StreamPath,
        InfoUrl = baseUrl.TrimEnd('/') + slot.InfoPath,
        ChannelLogoUrl = slot.ChannelLogoUrl,
        ChannelTileUrl = slot.ChannelTileUrl,
        ChannelBackgroundUrl = slot.ChannelBackgroundUrl,
        ShowImageUrl = slot.ShowImageUrl,
        DisplayImageUrl = slot.DisplayImageUrl,
        TrackName = slot.TrackName,
        ArtistName = slot.ArtistName,
        NowPlayingImageUrl = slot.NowPlayingImageUrl,
        Error = slot.LastError,
        UpdatedUtc = slot.UpdatedUtc
    };
}
