namespace SRXMDL.Models;

public enum LiveQueuePosition
{
    Played,
    NowPlaying,
    UpNext
}

public sealed class LiveCutEntry
{
    public string TrackName { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public DateTime ValidFromUtc { get; set; }
    public bool IsAd { get; set; }
    public LiveQueuePosition Position { get; set; }
    public string? ImageUrl { get; set; }
    public string ValidFromLocal => ValidFromUtc.ToLocalTime().ToString("HH:mm:ss");
    public string StatusLabel => Position switch
    {
        LiveQueuePosition.NowPlaying => "NOW",
        LiveQueuePosition.UpNext => "UP NEXT",
        LiveQueuePosition.Played => "RECENT",
        _ => "RECENT"
    };

    public string DisplayLine => string.IsNullOrWhiteSpace(ArtistName)
        ? TrackName
        : $"{ArtistName} — {TrackName}";
}

public sealed class ActiveLiveChannel
{
    public string ChannelId { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public int? ChannelNumber { get; set; }
    public string? ShowName { get; set; }
    public DateTime ActivatedUtc { get; set; } = DateTime.UtcNow;
}
