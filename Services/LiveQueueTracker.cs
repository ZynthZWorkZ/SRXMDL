using System.Collections.ObjectModel;
using SRXMDL.Models;

namespace SRXMDL.Services;

public sealed class LiveQueueTracker
{
    private const int MaxRecentDisplayed = 5;

    private readonly object _sync = new();
    private readonly Dictionary<string, LiveCutEntry> _cutsByKey = new(StringComparer.OrdinalIgnoreCase);
    private ActiveLiveChannel? _activeChannel;
    private string? _pendingLookAroundBody;

    public event Action? Changed;

    public ObservableCollection<LiveCutEntry> DisplayEntries { get; } = [];

    public string LookAroundUrl { get; set; } =
        "https://lookaround-cache-prod.streaming.siriusxm.com/playbackservices/v1/live/lookAround";

    public bool IsLiveActive
    {
        get { lock (_sync) return _activeChannel != null; }
    }

    public ActiveLiveChannel? ActiveChannel
    {
        get { lock (_sync) return _activeChannel; }
    }

    public LiveCutEntry? GetNowPlayingCut()
    {
        lock (_sync)
        {
            return _cutsByKey.Values
                .Where(c => !c.IsAd && c.Position == LiveQueuePosition.NowPlaying)
                .OrderByDescending(c => c.ValidFromUtc)
                .FirstOrDefault();
        }
    }

    public int UpNextCount
    {
        get
        {
            lock (_sync)
            {
                return _cutsByKey.Values.Count(c => !c.IsAd && c.Position == LiveQueuePosition.UpNext);
            }
        }
    }

    public int RecentlyPlayedCount
    {
        get
        {
            lock (_sync)
            {
                return _cutsByKey.Values.Count(c => !c.IsAd && c.Position == LiveQueuePosition.Played);
            }
        }
    }

    public void SetPendingLookAroundBody(string body) =>
        _pendingLookAroundBody = body;

    public string? TakePendingLookAroundBody()
    {
        var body = _pendingLookAroundBody;
        _pendingLookAroundBody = null;
        return body;
    }

    public void SetActiveLiveChannel(string channelId, string channelName, int? channelNumber, string? showName)
    {
        lock (_sync)
        {
            if (_activeChannel?.ChannelId == channelId)
            {
                _activeChannel.ChannelName = channelName;
                _activeChannel.ChannelNumber = channelNumber;
                if (!string.IsNullOrWhiteSpace(showName))
                    _activeChannel.ShowName = showName;
                Changed?.Invoke();
                return;
            }

            _activeChannel = new ActiveLiveChannel
            {
                ChannelId = channelId,
                ChannelName = channelName,
                ChannelNumber = channelNumber,
                ShowName = showName,
                ActivatedUtc = DateTime.UtcNow
            };
            _cutsByKey.Clear();
        }

        RebuildDisplayEntries();
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_sync)
        {
            _activeChannel = null;
            _cutsByKey.Clear();
            _pendingLookAroundBody = null;
        }

        DisplayEntries.Clear();
        Changed?.Invoke();
    }

    public void MergeLookAroundCuts(string channelId, IEnumerable<LiveCutEntry> incomingCuts, string? showName)
    {
        lock (_sync)
        {
            if (_activeChannel == null || !string.Equals(_activeChannel.ChannelId, channelId, StringComparison.OrdinalIgnoreCase))
                return;

            if (!string.IsNullOrWhiteSpace(showName))
                _activeChannel.ShowName = showName;

            foreach (var cut in incomingCuts)
            {
                var key = BuildCutKey(cut.ArtistName, cut.TrackName, cut.ValidFromUtc);
                _cutsByKey[key] = cut;
            }

            PruneStaleCutsLocked();
        }

        RebuildDisplayEntries();
        Changed?.Invoke();
    }

    /// <summary>
    /// Re-evaluates NOW vs UP NEXT as wall-clock time passes (no new network data needed).
    /// </summary>
    public void RefreshPositions()
    {
        RebuildDisplayEntries();
        Changed?.Invoke();
    }

    private void PruneStaleCutsLocked()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-25);
        var staleKeys = _cutsByKey
            .Where(pair => pair.Value.ValidFromUtc < cutoff)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in staleKeys)
            _cutsByKey.Remove(key);
    }

    private void RebuildDisplayEntries()
    {
        List<LiveCutEntry> ordered;
        lock (_sync)
        {
            ordered = _cutsByKey.Values
                .Where(c => !c.IsAd)
                .OrderBy(c => c.ValidFromUtc)
                .ToList();

            if (ordered.Count == 0)
            {
                DisplayEntries.Clear();
                return;
            }

            var nowIndex = ResolveNowPlayingIndex(ordered, DateTime.UtcNow);
            if (nowIndex < 0)
                nowIndex = 0;

            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Position = nowIndex switch
                {
                    < 0 => LiveQueuePosition.UpNext,
                    var idx when i < idx => LiveQueuePosition.Played,
                    var idx when i == idx => LiveQueuePosition.NowPlaying,
                    _ => LiveQueuePosition.UpNext
                };
            }
        }

        var played = ordered.Where(e => e.Position == LiveQueuePosition.Played).ToList();
        if (played.Count > MaxRecentDisplayed)
            played = played.Skip(played.Count - MaxRecentDisplayed).ToList();

        DisplayEntries.Clear();
        foreach (var entry in played)
            DisplayEntries.Add(entry);
        foreach (var entry in ordered.Where(e => e.Position is LiveQueuePosition.NowPlaying or LiveQueuePosition.UpNext))
            DisplayEntries.Add(entry);
    }

    /// <summary>
    /// The latest cut whose scheduled start is at or before now is currently playing.
    /// Any later cuts in the queue are up next (often several in one lookAround payload).
    /// </summary>
    internal static int ResolveNowPlayingIndex(IReadOnlyList<LiveCutEntry> ordered, DateTime nowUtc)
    {
        if (ordered.Count == 0)
            return -1;

        var grace = TimeSpan.FromSeconds(20);
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            if (ordered[i].ValidFromUtc <= nowUtc + grace)
                return i;
        }

        return -1;
    }

    private static string BuildCutKey(string artist, string track, DateTime validFromUtc) =>
        $"{artist}|{track}|{validFromUtc:O}";
}
