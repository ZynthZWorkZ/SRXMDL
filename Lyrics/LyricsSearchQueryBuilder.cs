using SRXMDL.Models;

namespace SRXMDL.Lyrics;

/// <summary>
/// Builds LyricsFetch search queries in the recommended <c>Artist - Song Title</c> format.
/// </summary>
internal static class LyricsSearchQueryBuilder
{
    private const string ArtistTitleSeparator = " - ";

    public static string Build(string trackName, string? artistName) =>
        BuildWithDetails(trackName, artistName).Query;

    public static LyricsSearchQueryDetails BuildWithDetails(string trackName, string? artistName)
    {
        var title = Normalize(trackName);
        if (string.IsNullOrWhiteSpace(title))
            return new LyricsSearchQueryDetails(string.Empty, "empty track name");

        if (ContainsArtistTitleSeparator(title))
            return new LyricsSearchQueryDetails(title, "track already in Artist - Title format");

        var artist = Normalize(artistName);
        if (IsUsableArtist(artist))
        {
            return new LyricsSearchQueryDetails(
                $"{artist}{ArtistTitleSeparator}{title}",
                "built as Artist - Title from resolved artist");
        }

        return new LyricsSearchQueryDetails(title, "title only (no artist metadata available)");
    }

    /// <summary>
    /// Resolves the performing artist for lyrics search.
    /// Priority: Artist - Title in track name → stream capture → now playing station name.
    /// </summary>
    public static LyricsArtistResolution ResolveArtist(
        string trackName,
        string? stationName,
        IEnumerable<StreamEntry> streamEntries)
    {
        var title = Normalize(trackName);
        if (title.Length == 0)
            return new LyricsArtistResolution(null, "empty track name");

        if (ContainsArtistTitleSeparator(title))
        {
            var parts = title.Split([ArtistTitleSeparator], 2, StringSplitOptions.None);
            if (parts.Length == 2 && IsUsableArtist(parts[0]))
                return new LyricsArtistResolution(parts[0].Trim(), "parsed Artist - Title from track name");
        }

        var streamMatch = streamEntries.LastOrDefault(e =>
            string.Equals(e.TrackName, title, StringComparison.OrdinalIgnoreCase) &&
            IsUsableArtist(e.ArtistName));

        if (streamMatch != null)
        {
            return new LyricsArtistResolution(
                streamMatch.ArtistName.Trim(),
                "stream capture metadata");
        }

        var station = Normalize(stationName);
        if (IsUsableStationArtist(station, title))
        {
            return new LyricsArtistResolution(
                station,
                "now playing station name (no stream artist yet)");
        }

        return new LyricsArtistResolution(null, "no artist found");
    }

    /// <summary>
    /// Resolves artist from the live radio What's Next queue (lookAround NOW cut).
    /// </summary>
    public static LyricsArtistResolution ResolveArtistForLiveRadio(LiveCutEntry? cut)
    {
        if (cut == null || string.IsNullOrWhiteSpace(cut.TrackName))
            return new LyricsArtistResolution(null, "live radio queue empty");

        var artist = Normalize(cut.ArtistName);
        if (IsUsableArtist(artist))
            return new LyricsArtistResolution(artist, "live radio What's Next queue");

        return new LyricsArtistResolution(null, "live radio queue has no artist");
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static bool ContainsArtistTitleSeparator(string value) =>
        value.Contains(ArtistTitleSeparator, StringComparison.Ordinal);

    private static bool IsUsableArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return false;

        return artist.Trim() is not ("Unknown" or "Unknown Artist");
    }

    private static bool IsUsableStationArtist(string station, string trackTitle)
    {
        if (!IsUsableArtist(station))
            return false;

        if (string.Equals(station, "No station selected", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(station, "Unknown Station", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Don't treat the song title itself as the artist.
        if (string.Equals(station, trackTitle, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}

internal readonly record struct LyricsSearchQueryDetails(string Query, string Source);

internal readonly record struct LyricsArtistResolution(string? Artist, string Source);
