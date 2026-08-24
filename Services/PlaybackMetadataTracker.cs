using SRXMDL.Models;

namespace SRXMDL.Services;

/// <summary>
/// Tracks the currently playing track from network captures (tuneSource, peek, Litix, Conviva).
/// Mobile hides player title/station text, so API traffic is the reliable source.
/// </summary>
public sealed class PlaybackMetadataTracker
{
    private readonly object _sync = new();
    private NowPlaying _current = new();
    private DateTime _lastUpdatedUtc = DateTime.MinValue;

    public NowPlaying Snapshot()
    {
        lock (_sync)
        {
            return new NowPlaying
            {
                TrackName = _current.TrackName,
                StationName = _current.StationName,
                AlbumArtUrl = _current.AlbumArtUrl,
                AlbumName = _current.AlbumName,
                DurationMs = _current.DurationMs
            };
        }
    }

    public void UpdateFromArtistTrack(
        string trackName,
        string artistName,
        string? stationName = null,
        string? albumName = null,
        long? durationMs = null,
        string? albumArtUrl = null,
        string source = "network")
    {
        if (string.IsNullOrWhiteSpace(trackName))
            return;

        lock (_sync)
        {
            _current.TrackName = trackName.Trim();
            if (!string.IsNullOrWhiteSpace(stationName))
                _current.StationName = stationName.Trim();
            else if (!string.IsNullOrWhiteSpace(artistName))
                _current.StationName = artistName.Trim();

            if (!string.IsNullOrWhiteSpace(albumName))
                _current.AlbumName = albumName.Trim();
            if (durationMs is > 0)
                _current.DurationMs = durationMs;
            if (!string.IsNullOrWhiteSpace(albumArtUrl))
                _current.AlbumArtUrl = albumArtUrl.Trim();

            _lastUpdatedUtc = DateTime.UtcNow;
        }
    }

    public void UpdateStationName(string? stationName)
    {
        if (string.IsNullOrWhiteSpace(stationName))
            return;

        lock (_sync)
        {
            _current.StationName = stationName.Trim();
            _lastUpdatedUtc = DateTime.UtcNow;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _current = new NowPlaying();
            _lastUpdatedUtc = DateTime.MinValue;
        }
    }

    public bool HasRecentMetadata(TimeSpan maxAge) =>
        DateTime.UtcNow - _lastUpdatedUtc <= maxAge;
}
