namespace SRXMDL.Services;

public enum RadioProxyState
{
    Waiting,
    Ready,
    Stale,
    Failed
}

public sealed class RadioProxySlot
{
    public string SlotId { get; init; } = string.Empty;
    public string ChannelId { get; init; } = string.Empty;
    public string ChannelName { get; set; } = "Live Channel";
    public int? ChannelNumber { get; set; }
    public string? ShowName { get; set; }
    public string? SourcePlaylistUrl { get; set; }
    public byte[]? DrmKeyBytes { get; set; }
    public RadioProxyState State { get; set; } = RadioProxyState.Waiting;
    public int? ChannelNumberCanonical { get; set; }
    public string? ChannelLogoUrl { get; set; }
    public string? ChannelTileUrl { get; set; }
    public string? ChannelBackgroundUrl { get; set; }
    public string? ShowImageUrl { get; set; }
    public bool IsPlayByPlay { get; set; }
    public string? TrackName { get; set; }
    public string? ArtistName { get; set; }
    public string? NowPlayingImageUrl { get; set; }
    public bool Exposed { get; set; } = true;
    public bool IsDefault { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }

    public bool CanStream =>
        Exposed &&
        State == RadioProxyState.Ready &&
        !string.IsNullOrWhiteSpace(SourcePlaylistUrl);

    public string StreamPath => $"/radio/{SlotId}/stream.m3u8";
    public string InfoPath => $"/api/channels/{SlotId}";

    public string? DisplayImageUrl =>
        ChannelTileUrl ?? ChannelLogoUrl ?? ShowImageUrl ?? NowPlayingImageUrl;
}

public sealed class RadioProxyCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RadioProxySlot> _bySlotId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RadioProxySlot> _byChannelId = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public IReadOnlyList<RadioProxySlot> GetAllSlots()
    {
        lock (_sync)
            return _bySlotId.Values.OrderByDescending(s => s.IsDefault).ThenBy(s => s.ChannelNumber).ToList();
    }

    public RadioProxySlot? GetSlot(string slotId)
    {
        lock (_sync)
            return _bySlotId.TryGetValue(slotId, out var slot) ? slot : null;
    }

    public RadioProxySlot? GetSlotByChannelId(string channelId)
    {
        lock (_sync)
            return _byChannelId.TryGetValue(channelId, out var slot) ? slot : null;
    }

    public bool IsRegistered(string channelId)
    {
        lock (_sync)
            return _byChannelId.ContainsKey(channelId);
    }

    public RadioProxySlot RegisterChannel(
        string channelId,
        string channelName,
        int? channelNumber,
        string? showName,
        bool makeDefault = false)
    {
        lock (_sync)
        {
            if (_byChannelId.TryGetValue(channelId, out var existing))
            {
                existing.ChannelName = channelName;
                existing.ChannelNumber = channelNumber ?? existing.ChannelNumber;
                existing.ShowName = showName ?? existing.ShowName;
                existing.UpdatedUtc = DateTime.UtcNow;
                if (makeDefault)
                {
                    foreach (var registered in _bySlotId.Values)
                        registered.IsDefault = false;
                    existing.IsDefault = true;
                }

                NotifyChanged();
                return existing;
            }

            if (makeDefault)
            {
                foreach (var registered in _bySlotId.Values)
                    registered.IsDefault = false;
            }

            var slotId = CreateUniqueSlotId(channelId);
            var slot = new RadioProxySlot
            {
                SlotId = slotId,
                ChannelId = channelId,
                ChannelName = channelName,
                ChannelNumber = channelNumber,
                ShowName = showName,
                IsDefault = makeDefault,
                Exposed = true,
                State = RadioProxyState.Waiting
            };

            _bySlotId[slotId] = slot;
            _byChannelId[channelId] = slot;
            NotifyChanged();
            return slot;
        }
    }

    public void ApplyChannelDetails(string channelId, RadioChannelDetails details)
    {
        lock (_sync)
        {
            if (!_byChannelId.TryGetValue(channelId, out var slot))
                return;

            if (!string.IsNullOrWhiteSpace(details.ChannelLogoUrl))
                slot.ChannelLogoUrl = details.ChannelLogoUrl;
            if (!string.IsNullOrWhiteSpace(details.ChannelTileUrl))
                slot.ChannelTileUrl = details.ChannelTileUrl;
            if (!string.IsNullOrWhiteSpace(details.ChannelBackgroundUrl))
                slot.ChannelBackgroundUrl = details.ChannelBackgroundUrl;
            if (details.ChannelNumberCanonical is > 0)
                slot.ChannelNumberCanonical = details.ChannelNumberCanonical;
            if (!string.IsNullOrWhiteSpace(details.ShowImageUrl))
                slot.ShowImageUrl = details.ShowImageUrl;

            slot.UpdatedUtc = DateTime.UtcNow;
            NotifyChanged();
        }
    }

    public void UpdateLookAroundInfo(
        string channelId,
        string? showName,
        string? showImageUrl,
        bool isPlayByPlay)
    {
        lock (_sync)
        {
            if (!_byChannelId.TryGetValue(channelId, out var slot))
                return;

            if (!string.IsNullOrWhiteSpace(showName))
                slot.ShowName = showName;
            if (!string.IsNullOrWhiteSpace(showImageUrl))
                slot.ShowImageUrl = showImageUrl;
            slot.IsPlayByPlay = isPlayByPlay;
            slot.UpdatedUtc = DateTime.UtcNow;
            NotifyChanged();
        }
    }

    public void UpdateNowPlaying(
        string channelId,
        string? trackName,
        string? artistName,
        string? imageUrl,
        string? showName = null)
    {
        lock (_sync)
        {
            if (!_byChannelId.TryGetValue(channelId, out var slot))
                return;

            slot.TrackName = trackName;
            slot.ArtistName = artistName;
            slot.NowPlayingImageUrl = imageUrl;
            if (!string.IsNullOrWhiteSpace(showName))
                slot.ShowName = showName;
            slot.UpdatedUtc = DateTime.UtcNow;
            NotifyChanged();
        }
    }

    public void SetSourceUrl(string channelId, string? m3u8Url)
    {
        lock (_sync)
        {
            if (!_byChannelId.TryGetValue(channelId, out var slot))
                return;

            slot.SourcePlaylistUrl = m3u8Url;
            slot.State = string.IsNullOrWhiteSpace(m3u8Url) ? RadioProxyState.Waiting : RadioProxyState.Ready;
            slot.LastError = null;
            slot.UpdatedUtc = DateTime.UtcNow;
            NotifyChanged();
        }
    }

    public void SetDrmKey(string channelId, byte[]? keyBytes)
    {
        lock (_sync)
        {
            if (!_byChannelId.TryGetValue(channelId, out var slot))
                return;

            slot.DrmKeyBytes = keyBytes;
            slot.UpdatedUtc = DateTime.UtcNow;
            NotifyChanged();
        }
    }

    public void SetState(string channelId, RadioProxyState state, string? error = null)
    {
        lock (_sync)
        {
            if (!_byChannelId.TryGetValue(channelId, out var slot))
                return;

            slot.State = state;
            slot.LastError = error;
            slot.UpdatedUtc = DateTime.UtcNow;
            NotifyChanged();
        }
    }

    public bool Unregister(string slotId)
    {
        lock (_sync)
        {
            if (!_bySlotId.TryGetValue(slotId, out var slot))
                return false;

            _bySlotId.Remove(slotId);
            _byChannelId.Remove(slot.ChannelId);
            NotifyChanged();
            return true;
        }
    }

    public RadioProxySlot? GetDefaultSlot()
    {
        lock (_sync)
            return _bySlotId.Values.FirstOrDefault(s => s.IsDefault) ??
                   _bySlotId.Values.FirstOrDefault(s => s.CanStream);
    }

    private string CreateUniqueSlotId(string channelId)
    {
        var baseId = channelId.Replace("-", "", StringComparison.Ordinal);
        if (baseId.Length > 12)
            baseId = baseId[..12];
        baseId = baseId.ToLowerInvariant();

        var candidate = baseId;
        var suffix = 1;
        while (_bySlotId.TryGetValue(candidate, out var existing) &&
               !existing.ChannelId.Equals(channelId, StringComparison.OrdinalIgnoreCase))
        {
            candidate = $"{baseId}{suffix}";
            suffix++;
        }

        return candidate;
    }

    private void NotifyChanged() => Changed?.Invoke();
}
