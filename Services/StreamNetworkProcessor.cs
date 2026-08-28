using System.IO;
using System.Net.Http;
using System.Text.Json;
using Serilog;
using SRXMDL.Models;

namespace SRXMDL.Services;

public sealed class StreamNetworkProcessor
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public async Task HandleResponseAsync(string url, NetworkRequestInfo? request, IStreamCaptureHost host)
    {
        Log.Debug("CDP response observed: {Url}", url);

        if (url.StartsWith("https://www.siriusxm.com/player/artist-station", StringComparison.OrdinalIgnoreCase))
            await host.ProcessArtistStationUrlAsync(url);

        if (!host.IsMonitoring)
            return;

        if (ShouldSkipUrl(url))
            return;

        if (url.Contains(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            await HandleMp3Async(url, host);
            return;
        }

        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && !url.Contains("named"))
        {
            await HandleUnnamedMp4Async(url, host);
            return;
        }

        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            await HandleM3u8Async(url, host);
            return;
        }

        if (url.Contains("api.edge-gateway.siriusxm.com/stations/v1/station-feedback/station/type/artist-station/id"))
        {
            await HandleArtistStationFeedbackAsync(request);
            return;
        }

        if (url.Contains("api.edge-gateway.siriusxm.com/page/v1/page/artist-station"))
        {
            await HandleArtistStationPageAsync(request);
            return;
        }

        if (url.Contains("api.edge-gateway.siriusxm.com/playback/play/v1/tuneSource"))
        {
            await HandleTuneSourceAsync(url, request, host);
            return;
        }

        if (url.EndsWith("conviva.com/0/wsg", StringComparison.OrdinalIgnoreCase))
        {
            await HandleConvivaWsgAsync(request, host);
            return;
        }

        if (url.Contains("litix.io", StringComparison.OrdinalIgnoreCase))
        {
            await HandleLitixAsync(request, host);
            return;
        }

        if (url.Contains("api.edge-gateway.siriusxm.com/playback/play/v1/peek"))
        {
            await HandlePeekAsync(url, request, host);
            return;
        }

        if (url.Contains("/live/lookAround", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("lookAroundEpisodes", StringComparison.OrdinalIgnoreCase))
        {
            await HandleLookAroundAsync(url, host);
            return;
        }

        if (url.Contains("api.edge-gateway.siriusxm.com/playback/key/v1/"))
        {
            await HandlePlaybackKeyAsync(url, request);
            return;
        }

        if (host.CaptureBearer && url.Contains("api.edge-gateway.siriusxm.com/user-event/v1/events/submit"))
            await HandleUserEventBearerCaptureAsync(request, host);
    }

    public async Task SendTuneSourceRequestAsync(string url, string payload, string authToken, IStreamCaptureHost host)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", authToken);
            httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(url, content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to get response from tuneSource endpoint. Status code: {StatusCode}", response.StatusCode);
                return;
            }

            var jsonElement = JsonSerializer.Deserialize<JsonElement>(responseBody);
            var formattedJson = JsonSerializer.Serialize(jsonElement, IndentedJson);
            await File.WriteAllTextAsync("Stations/Playlist.json", formattedJson);
            Log.Information("Response saved to Stations/Playlist.json");

            if (jsonElement.TryGetProperty("type", out var typeElement))
            {
                var type = typeElement.GetString();
                switch (type)
                {
                    case "episode-podcast":
                        await ProcessPodcastEpisodeAsync(jsonElement, host);
                        break;
                    case "episode-video":
                        await ProcessVideoEpisodeAsync(jsonElement, host);
                        break;
                    case "episode-audio":
                        await ProcessAudioEpisodeAsync(jsonElement, host);
                        break;
                    case "channel-linear":
                        ProcessChannelLinearTuneSource(jsonElement, host);
                        await TryProcessPendingLookAroundAsync(host);
                        Log.Information("Channel Linear detected in playlist: {Id}",
                            jsonElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : "unknown");
                        break;
                    default:
                        if (jsonElement.TryGetProperty("streams", out var streamsElement) &&
                            streamsElement.ValueKind == JsonValueKind.Array)
                        {
                            UpdateNowPlayingFromFirstStream(streamsElement, host);
                            await ProcessTuneSourceStreamsAsync(streamsElement, host);
                        }
                        break;
                }

                if (type != "channel-linear")
                    host.LiveQueueTracker.Clear();

                return;
            }

            if (jsonElement.TryGetProperty("streams", out var fallbackStreams) &&
                fallbackStreams.ValueKind == JsonValueKind.Array)
            {
                UpdateNowPlayingFromFirstStream(fallbackStreams, host);
                await ProcessTuneSourceStreamsAsync(fallbackStreams, host);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error making POST request to tuneSource endpoint");
        }
    }

    private static bool ShouldSkipUrl(string url) =>
        url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("imgsrv-sxm");

    private static async Task HandleMp3Async(string url, IStreamCaptureHost host)
    {
        if (host.IsDuplicateStream(url, out _))
        {
            Log.Debug("Skipping duplicate MP3 file: {Url}", url);
            return;
        }

        Log.Information("MP3 traffic detected: {Url}", url);
        await host.RunOnUiAsync(() =>
        {
            host.StreamEntries.Add(new StreamEntry
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                StreamType = "mp3",
                Url = url,
                TrackName = "Unnamed MP3",
                ArtistName = "Unknown",
                PreferredImageUrl = null
            });
            host.UpdateTotalCapturedCount();
        });
    }

    private static async Task HandleUnnamedMp4Async(string url, IStreamCaptureHost host)
    {
        if (host.IsDuplicateStream(url, out _))
        {
            Log.Debug("Skipping duplicate MP4 file: {Url}", url);
            return;
        }

        Log.Information("Unnamed MP4 traffic detected: {Url}", url);
        await host.RunOnUiAsync(() =>
        {
            host.StreamEntries.Add(new StreamEntry
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                StreamType = "mp4",
                Url = url,
                TrackName = "Unnamed MP4",
                ArtistName = "Unknown",
                PreferredImageUrl = null
            });
            host.UpdateTotalCapturedCount();
        });
    }

    private static async Task HandleM3u8Async(string url, IStreamCaptureHost host)
    {
        if (host.IsDuplicateStream(url, out _))
        {
            Log.Debug("Skipping duplicate M3U8 file: {Url}", url);
            return;
        }

        Log.Information("M3U8 traffic detected: {Url}", url);

        if (host.LiveQueueTracker.IsLiveActive && !LiveRadioRecorder.IsVodM3u8(url))
            host.SetLiveStreamUrl(url);

        await host.RunOnUiAsync(() =>
        {
            host.StreamEntries.Add(new StreamEntry
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                StreamType = "m3u8",
                Url = url,
                TrackName = "Unnamed M3U8",
                ArtistName = "Unknown",
                PreferredImageUrl = null
            });
            host.UpdateTotalCapturedCount();
        });
    }

    private static async Task HandleArtistStationFeedbackAsync(NetworkRequestInfo? request)
    {
        Log.Information("Station feedback request detected: {Url}", request?.Url);
        var authToken = request?.GetAuthorization();
        if (string.IsNullOrEmpty(authToken))
            return;

        Directory.CreateDirectory("Artist");
        await File.WriteAllTextAsync("Artist/ArtistAuth.txt", authToken);
        Log.Information("Auth token saved to Artist/ArtistAuth.txt");
    }

    private static async Task HandleArtistStationPageAsync(NetworkRequestInfo? request)
    {
        Log.Information("Artist station page API request detected: {Url}", request?.Url);
        var authToken = request?.GetAuthorization();
        if (string.IsNullOrEmpty(authToken))
            return;

        Directory.CreateDirectory("Artist");
        await File.WriteAllTextAsync("Artist/ArtistAuth.txt", authToken);
        Log.Information("Auth token saved to Artist/ArtistAuth.txt");
    }

    private async Task HandleTuneSourceAsync(string url, NetworkRequestInfo? request, IStreamCaptureHost host)
    {
        Log.Information("TuneSource request detected: {Url}", url);

        var authToken = request?.GetAuthorization() ?? string.Empty;
        var requestPayload = request?.PostData ?? string.Empty;

        host.LastTuneSourceUrl = url;
        host.LastTuneSourcePayload = requestPayload;
        host.LastTuneSourceAuthToken = authToken;

        TryActivateLiveChannelFromRequest(requestPayload, host);
        await TryProcessPendingLookAroundAsync(host);

        if (string.IsNullOrEmpty(authToken) || string.IsNullOrEmpty(requestPayload))
            return;

        Directory.CreateDirectory("Stations");
        if (!File.Exists("Stations/tunesource.txt"))
            File.Create("Stations/tunesource.txt").Dispose();

        await File.WriteAllTextAsync("Stations/tunesource.txt", authToken);
        Log.Information("Auth token saved to Stations/tunesource.txt");

        try
        {
            await SendTuneSourceRequestAsync(url, requestPayload, authToken, host);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error making POST request to tuneSource endpoint");
        }
    }

    private static async Task HandleConvivaWsgAsync(NetworkRequestInfo? request, IStreamCaptureHost host)
    {
        Log.Information("Conviva WSG request detected: {Url}", request?.Url);
        var wsgPayload = request?.PostData;
        if (string.IsNullOrEmpty(wsgPayload))
            return;

        try
        {
            var jsonElement = JsonSerializer.Deserialize<JsonElement>(wsgPayload);
            var hasRequiredFwv = HasRequiredFwv(jsonElement);
            var hasNewOldObjects = HasNewOldObjects(jsonElement);

            if (!hasRequiredFwv || hasNewOldObjects)
            {
                if (!hasRequiredFwv)
                    Log.Debug("Skipping WSG payload - does not contain required fwv value");
                else
                    Log.Debug("Skipping WSG payload - contains new/old objects in evs array");
                return;
            }

            Directory.CreateDirectory("Stations");
            var formattedJson = JsonSerializer.Serialize(jsonElement, IndentedJson);
            await File.WriteAllTextAsync("Stations/Now.json", formattedJson);
            Log.Information("WSG payload with required fwv saved to Stations/Now.json");

            var artistName = GetJsonString(jsonElement, "artistName")
                ?? (jsonElement.TryGetProperty("tags", out var tags) && tags.TryGetProperty("artistName", out var tagsArtist)
                    ? tagsArtist.GetString() ?? ""
                    : "");
            var trackName = GetJsonString(jsonElement, "an") ?? "";
            var streamUrl = GetJsonString(jsonElement, "url") ?? "";

            if (!string.IsNullOrWhiteSpace(trackName))
            {
                host.MetadataTracker.UpdateFromArtistTrack(
                    trackName,
                    artistName,
                    source: "conviva");
            }

            if (string.IsNullOrEmpty(streamUrl))
                return;

            await host.RunOnUiAsync(() =>
            {
                var existingEntry = host.StreamEntries.FirstOrDefault(e => e.Url == streamUrl);
                if (existingEntry != null)
                {
                    if (existingEntry.TrackName == "Unnamed MP4")
                    {
                        existingEntry.TrackName = trackName;
                        existingEntry.ArtistName = artistName;
                        Log.Information("Updated unnamed stream entry with track info: {Track} - {Artist}", trackName, artistName);
                    }
                }
                else
                {
                    var duplicateEntry = host.StreamEntries.FirstOrDefault(e =>
                        e.TrackName == trackName &&
                        e.ArtistName == artistName &&
                        !string.IsNullOrEmpty(trackName) &&
                        !string.IsNullOrEmpty(artistName));

                    if (duplicateEntry == null)
                    {
                        host.StreamEntries.Add(new StreamEntry
                        {
                            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                            StreamType = "mp4",
                            Url = streamUrl,
                            TrackName = trackName,
                            ArtistName = artistName,
                            PreferredImageUrl = null
                        });
                        Log.Information("Added new stream entry: {Track} - {Artist}", trackName, artistName);
                    }
                    else
                    {
                        Log.Debug("Skipping duplicate stream entry: {Track} - {Artist}", trackName, artistName);
                    }
                }

                host.UpdateTotalCapturedCount();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing WSG payload");
        }
    }

    private async Task HandlePeekAsync(string url, NetworkRequestInfo? request, IStreamCaptureHost host)
    {
        Log.Information("Peek request detected: {Url}", url);

        var peekAuthToken = request?.GetAuthorization() ?? string.Empty;
        var peekRequestPayload = request?.PostData ?? string.Empty;

        if (string.IsNullOrEmpty(peekAuthToken) || string.IsNullOrEmpty(peekRequestPayload))
            return;

        Directory.CreateDirectory("Stations");
        await File.WriteAllTextAsync("Stations/peek.txt", peekAuthToken);
        Log.Information("Auth token saved to Stations/peek.txt");

        try
        {
            var jsonElement = JsonSerializer.Deserialize<JsonElement>(peekRequestPayload);
            var formattedJson = JsonSerializer.Serialize(jsonElement, IndentedJson);
            await File.WriteAllTextAsync("Stations/peek_payload.json", formattedJson);
            Log.Information("Peek payload saved to Stations/peek_payload.json");

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", peekAuthToken);
            httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

            var content = new StringContent(peekRequestPayload, System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(url, content);
            var peekResponseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to get response from peek endpoint. Status code: {StatusCode}", response.StatusCode);
                return;
            }

            var responseElement = JsonSerializer.Deserialize<JsonElement>(peekResponseBody);
            var formattedResponse = JsonSerializer.Serialize(responseElement, IndentedJson);
            await File.WriteAllTextAsync("Stations/peek_response.json", formattedResponse);
            Log.Information("Peek response saved to Stations/peek_response.json");

            if (responseElement.TryGetProperty("streams", out var streamsElement) &&
                streamsElement.ValueKind == JsonValueKind.Array)
            {
                await ProcessPeekStreamsAsync(streamsElement, host);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing peek request");
        }
    }

    private static async Task HandlePlaybackKeyAsync(string url, NetworkRequestInfo? request)
    {
        Log.Information("Playback key request detected: {Url}", url);
        var keyAuthToken = request?.GetAuthorization();

        if (string.IsNullOrEmpty(keyAuthToken))
        {
            Log.Warning("Missing auth token for playback key request; skipping fetch.");
            return;
        }

        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", keyAuthToken);
            httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

            var response = await httpClient.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Playback key request failed: {StatusCode}", response.StatusCode);
                return;
            }

            Directory.CreateDirectory("HLSKey");
            await File.WriteAllTextAsync("HLSKey/authorization Bearer.txt", keyAuthToken);

            var jsonElement = JsonSerializer.Deserialize<JsonElement>(body);
            var formatted = JsonSerializer.Serialize(jsonElement, IndentedJson);
            await File.WriteAllTextAsync("HLSKey/response.json", formatted);
            Log.Information("Playback key saved to HLSKey/response.json");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error fetching playback key response");
        }
    }

    private static async Task HandleUserEventBearerCaptureAsync(NetworkRequestInfo? request, IStreamCaptureHost host)
    {
        try
        {
            var bearerToken = request?.GetAuthorization();
            if (string.IsNullOrWhiteSpace(bearerToken) ||
                !bearerToken.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
                return;

            Directory.CreateDirectory("Login");
            await File.WriteAllTextAsync("Login/authorization Bearer.txt", bearerToken);
            host.CaptureBearer = false;

            if (host.AttemptAutoLoginWithCredsAsync != null)
                _ = Task.Run(() => host.AttemptAutoLoginWithCredsAsync(bearerToken));

            Log.Information("Captured bearer token from user-event submit and saved to Login/authorization Bearer.txt");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error capturing bearer token from user-event submit");
        }
    }

    private static async Task ProcessPeekStreamsAsync(JsonElement streamsElement, IStreamCaptureHost host)
    {
        foreach (var stream in streamsElement.EnumerateArray())
        {
            if (!stream.TryGetProperty("metadata", out var metadata) ||
                !metadata.TryGetProperty("artist", out var artist) ||
                !artist.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameElement) ||
                    !item.TryGetProperty("artistName", out var artistNameElement))
                    continue;

                var trackName = nameElement.GetString();
                var artistName = artistNameElement.GetString();
                var albumName = GetJsonString(item, "albumName");
                var durationMs = GetJsonInt64(item, "duration");
                var trackId = GetJsonString(item, "id") ?? GetJsonString(stream, "id");

                if (!stream.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var urlEntry in urls.EnumerateArray())
                {
                    if (!urlEntry.TryGetProperty("url", out var urlElement) ||
                        !urlEntry.TryGetProperty("isPrimary", out var isPrimaryElement) ||
                        !isPrimaryElement.GetBoolean())
                        continue;

                    var streamUrl = urlElement.GetString();
                    if (string.IsNullOrEmpty(streamUrl))
                        continue;

                    if (host.IsDuplicateStream(streamUrl, out var existingEntry))
                    {
                        if (existingEntry?.TrackName == "Unnamed MP4")
                        {
                            await host.RunOnUiAsync(() =>
                            {
                                existingEntry.TrackName = trackName ?? existingEntry.TrackName;
                                existingEntry.ArtistName = artistName ?? existingEntry.ArtistName;
                                existingEntry.AlbumName = albumName ?? existingEntry.AlbumName;
                                existingEntry.DurationMs = durationMs ?? existingEntry.DurationMs;
                                existingEntry.TrackId = trackId ?? existingEntry.TrackId;
                                host.RefreshStreamList();
                            });
                            Log.Information("Updated unnamed entry with title: {TrackName} - {ArtistName}", trackName, artistName);
                        }

                        Log.Debug("Skipping duplicate stream URL: {Url}", streamUrl);
                        continue;
                    }

                    var preferredImageUrl = ExtractPreferredImageFromStream(stream);
                    await host.RunOnUiAsync(() =>
                    {
                        host.StreamEntries.Add(new StreamEntry
                        {
                            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                            StreamType = "mp4",
                            Url = streamUrl,
                            TrackName = trackName ?? "Unknown",
                            ArtistName = artistName ?? "Unknown",
                            PreferredImageUrl = preferredImageUrl,
                            AlbumName = albumName,
                            DurationMs = durationMs,
                            TrackId = trackId
                        });
                        host.UpdateTotalCapturedCount();
                    });
                    break;
                }
            }
        }
    }

    private static async Task ProcessPodcastEpisodeAsync(JsonElement jsonElement, IStreamCaptureHost host)
    {
        Log.Information("Podcast episode detected in playlist: {Id}",
            jsonElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : "unknown");

        if (!jsonElement.TryGetProperty("streams", out var podcastStreamsElement) ||
            podcastStreamsElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var stream in podcastStreamsElement.EnumerateArray())
        {
            if (!stream.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var urlEntry in urls.EnumerateArray())
            {
                if (!urlEntry.TryGetProperty("isPrimary", out var isPrimaryElement) ||
                    !isPrimaryElement.GetBoolean() ||
                    !urlEntry.TryGetProperty("url", out var urlElement))
                    continue;

                var streamUrl = urlElement.GetString();
                if (string.IsNullOrEmpty(streamUrl))
                    continue;

                var episodeName = "Unknown Episode";
                var showName = "Unknown Show";
                string? preferredImageUrl = null;

                if (stream.TryGetProperty("metadata", out var metadata) &&
                    metadata.TryGetProperty("podcast", out var podcast) &&
                    podcast.TryGetProperty("episode", out var episode))
                {
                    if (episode.TryGetProperty("name", out var nameElement))
                        episodeName = nameElement.GetString() ?? episodeName;
                    if (episode.TryGetProperty("showName", out var showNameElement))
                        showName = showNameElement.GetString() ?? showName;
                    preferredImageUrl = ExtractPreferredImage(episode, "showImages");
                }

                await host.RunOnUiAsync(() =>
                {
                    host.StreamEntries.Add(new StreamEntry
                    {
                        Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                        StreamType = "podcast",
                        Url = streamUrl,
                        TrackName = episodeName,
                        ArtistName = showName,
                        PreferredImageUrl = preferredImageUrl
                    });
                    host.UpdateTotalCapturedCount();
                });
                break;
            }
        }
    }

    private static async Task ProcessVideoEpisodeAsync(JsonElement jsonElement, IStreamCaptureHost host)
    {
        Log.Information("Video episode detected in playlist: {Id}",
            jsonElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : "unknown");

        if (!jsonElement.TryGetProperty("streams", out var videoStreamsElement) ||
            videoStreamsElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var stream in videoStreamsElement.EnumerateArray())
        {
            if (!stream.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var urlEntry in urls.EnumerateArray())
            {
                if (!urlEntry.TryGetProperty("name", out var nameElement) ||
                    nameElement.GetString() != "primary" ||
                    !urlEntry.TryGetProperty("url", out var urlElement))
                    continue;

                var streamUrl = urlElement.GetString();
                if (string.IsNullOrEmpty(streamUrl))
                    continue;

                var episodeName = "Unknown Episode";
                var channelName = "Unknown Channel";
                string? preferredImageUrl = null;

                if (jsonElement.TryGetProperty("metadata", out var metadata) &&
                    metadata.TryGetProperty("vod", out var vod) &&
                    vod.TryGetProperty("episode", out var episode))
                {
                    if (episode.TryGetProperty("name", out var episodeNameElement))
                        episodeName = episodeNameElement.GetString() ?? episodeName;
                    if (vod.TryGetProperty("channelName", out var channelNameElement))
                        channelName = channelNameElement.GetString() ?? channelName;
                    preferredImageUrl = ExtractPreferredImage(episode, "images");
                }

                if (await TryAddOrUpdateM3u8EntryAsync(streamUrl, episodeName, channelName, preferredImageUrl, host))
                    break;
            }
        }
    }

    private static async Task ProcessAudioEpisodeAsync(JsonElement jsonElement, IStreamCaptureHost host)
    {
        Log.Information("Audio episode detected in playlist: {Id}",
            jsonElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : "unknown");

        if (!jsonElement.TryGetProperty("streams", out var audioStreamsElement) ||
            audioStreamsElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var stream in audioStreamsElement.EnumerateArray())
        {
            if (!stream.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var urlEntry in urls.EnumerateArray())
            {
                if (!urlEntry.TryGetProperty("name", out var nameElement) ||
                    nameElement.GetString() != "primary" ||
                    !urlEntry.TryGetProperty("url", out var urlElement))
                    continue;

                var streamUrl = urlElement.GetString();
                if (string.IsNullOrEmpty(streamUrl))
                    continue;

                var episodeName = "Unknown Episode";
                var channelName = "Unknown Channel";
                string? preferredImageUrl = null;

                if (stream.TryGetProperty("metadata", out var metadata) &&
                    metadata.TryGetProperty("aod", out var aod) &&
                    aod.TryGetProperty("episode", out var episode))
                {
                    if (episode.TryGetProperty("name", out var episodeNameElement))
                        episodeName = episodeNameElement.GetString() ?? episodeName;
                    if (aod.TryGetProperty("channelName", out var channelNameElement))
                        channelName = channelNameElement.GetString() ?? channelName;
                    preferredImageUrl = ExtractPreferredImage(episode, "images");
                }

                if (await TryAddOrUpdateM3u8EntryAsync(streamUrl, episodeName, channelName, preferredImageUrl, host))
                    break;
            }
        }
    }

    private static async Task ProcessTuneSourceStreamsAsync(JsonElement streamsElement, IStreamCaptureHost host)
    {
        foreach (var stream in streamsElement.EnumerateArray())
        {
            if (!stream.TryGetProperty("metadata", out var metadata))
                continue;

            var trackName = "Unknown";
            var artistName = "Unknown";
            string? preferredImageUrl = null;
            string? albumName = null;
            long? durationMs = null;
            string? trackId = null;

            if (metadata.TryGetProperty("artist", out var artist) &&
                artist.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("name", out var nameElement) ||
                        !item.TryGetProperty("artistName", out var artistNameElement))
                        continue;

                    trackName = nameElement.GetString() ?? trackName;
                    artistName = artistNameElement.GetString() ?? artistName;
                    preferredImageUrl = ExtractPreferredImage(item, "images");
                    albumName = GetJsonString(item, "albumName");
                    durationMs = GetJsonInt64(item, "duration");
                    trackId = GetJsonString(item, "id") ?? GetJsonString(stream, "id");
                    break;
                }
            }
            else if (metadata.TryGetProperty("aod", out var aod) &&
                     aod.TryGetProperty("episode", out var episode))
            {
                if (episode.TryGetProperty("name", out var epName))
                    trackName = epName.GetString() ?? trackName;

                if (aod.TryGetProperty("channelName", out var channelNameProp))
                    artistName = channelNameProp.GetString() ?? artistName;
                else if (episode.TryGetProperty("showName", out var showNameProp))
                    artistName = showNameProp.GetString() ?? artistName;

                preferredImageUrl = ExtractPreferredImage(episode, "images")
                    ?? ExtractPreferredImage(episode, "showImages");
                durationMs = GetJsonInt64(episode, "duration");
                trackId = GetJsonString(episode, "id") ?? GetJsonString(stream, "id");
            }

            if (!stream.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var urlEntry in urls.EnumerateArray())
            {
                if (!urlEntry.TryGetProperty("url", out var urlElement) ||
                    !urlEntry.TryGetProperty("isPrimary", out var isPrimaryElement) ||
                    !isPrimaryElement.GetBoolean())
                    continue;

                var streamUrl = urlElement.GetString();
                if (string.IsNullOrEmpty(streamUrl))
                    continue;

                if (host.IsDuplicateStream(streamUrl, out var existingEntry))
                {
                    if (existingEntry?.TrackName == "Unnamed MP4")
                    {
                        await host.RunOnUiAsync(() =>
                        {
                            existingEntry.TrackName = trackName;
                            existingEntry.ArtistName = artistName;
                            existingEntry.PreferredImageUrl = preferredImageUrl ?? existingEntry.PreferredImageUrl;
                            existingEntry.AlbumName = albumName ?? existingEntry.AlbumName;
                            existingEntry.DurationMs = durationMs ?? existingEntry.DurationMs;
                            existingEntry.TrackId = trackId ?? existingEntry.TrackId;
                            host.RefreshStreamList();
                        });
                        Log.Information("Updated unnamed entry with title: {TrackName} - {ArtistName}", trackName, artistName);
                    }

                    Log.Debug("Skipping duplicate stream URL: {Url}", streamUrl);
                    continue;
                }

                await host.RunOnUiAsync(() =>
                {
                    host.StreamEntries.Add(new StreamEntry
                    {
                        Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                        StreamType = "mp4",
                        Url = streamUrl,
                        TrackName = trackName,
                        ArtistName = artistName,
                        PreferredImageUrl = preferredImageUrl,
                        AlbumName = albumName,
                        DurationMs = durationMs,
                        TrackId = trackId
                    });
                    host.UpdateTotalCapturedCount();
                });
                break;
            }
        }
    }

    private static async Task<bool> TryAddOrUpdateM3u8EntryAsync(
        string streamUrl,
        string episodeName,
        string channelName,
        string? preferredImageUrl,
        IStreamCaptureHost host)
    {
        if (host.IsDuplicateStream(streamUrl, out var existingEntry))
        {
            if (existingEntry?.TrackName == "Unnamed M3U8")
            {
                await host.RunOnUiAsync(() =>
                {
                    existingEntry.TrackName = episodeName;
                    existingEntry.ArtistName = channelName;
                    existingEntry.PreferredImageUrl = preferredImageUrl;
                    host.RefreshStreamList();
                });
                Log.Information("Updated unnamed M3U8 entry with metadata: {TrackName} - {ArtistName}", episodeName, channelName);
            }

            Log.Debug("Skipping duplicate M3U8 stream URL: {Url}", streamUrl);
            return true;
        }

        await host.RunOnUiAsync(() =>
        {
            host.StreamEntries.Add(new StreamEntry
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                StreamType = "m3u8",
                Url = streamUrl,
                TrackName = episodeName,
                ArtistName = channelName,
                PreferredImageUrl = preferredImageUrl
            });
            host.UpdateTotalCapturedCount();
        });
        return true;
    }

    private static string? ExtractPreferredImageFromStream(JsonElement stream)
    {
        if (!stream.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("artist", out var artist) ||
            !artist.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() == 0)
            return null;

        return ExtractPreferredImage(items[0], "images");
    }

    private static string? ExtractPreferredImage(JsonElement element, string imagesProperty)
    {
        if (!element.TryGetProperty(imagesProperty, out var images) ||
            !images.TryGetProperty("tile", out var tile) ||
            !tile.TryGetProperty("aspect_1x1", out var aspect) ||
            !aspect.TryGetProperty("preferredImage", out var preferredImage) ||
            !preferredImage.TryGetProperty("url", out var imageUrl))
            return null;

        return StreamEntry.DecodeImageUrl(imageUrl.GetString());
    }

    private static bool HasRequiredFwv(JsonElement jsonElement)
    {
        if (jsonElement.TryGetProperty("pm", out var pmElement) &&
            pmElement.TryGetProperty("fwv", out var pmFwvElement) &&
            pmFwvElement.GetString() == "howler - 2.2.4")
            return true;

        return jsonElement.TryGetProperty("fwv", out var rootFwvElement) &&
               rootFwvElement.GetString() == "howler - 2.2.4";
    }

    private static bool HasNewOldObjects(JsonElement jsonElement)
    {
        if (!jsonElement.TryGetProperty("evs", out var evsElement) ||
            evsElement.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var ev in evsElement.EnumerateArray())
        {
            if (ev.TryGetProperty("new", out _) || ev.TryGetProperty("old", out _))
                return true;
        }

        return false;
    }

    private static string? GetJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) ? prop.GetString() : null;

    private static long? GetJsonInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var value)
            ? value
            : null;

    private static async Task HandleLitixAsync(NetworkRequestInfo? request, IStreamCaptureHost host)
    {
        var payload = request?.PostData;
        if (string.IsNullOrWhiteSpace(payload))
            return;

        try
        {
            foreach (var beacon in EnumerateLitixBeacons(payload))
            {
                var trackName = GetJsonString(beacon, "vtt");
                if (string.IsNullOrWhiteSpace(trackName))
                    continue;

                var stationName = GetJsonString(beacon, "vpd");
                var durationMs = GetJsonInt64(beacon, "vdu");

                host.MetadataTracker.UpdateFromArtistTrack(
                    trackName,
                    string.Empty,
                    stationName: stationName,
                    durationMs: durationMs,
                    source: "litix");

                Log.Debug("Litix now playing: {Track} on {Station}", trackName, stationName);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error parsing Litix playback beacon");
        }

        await Task.CompletedTask;
    }

    private static IEnumerable<JsonElement> EnumerateLitixBeacons(string payload)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(payload);
        }
        catch
        {
            yield break;
        }

        foreach (var element in WalkJsonTree(root))
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            if (element.TryGetProperty("vtt", out var titleProp) &&
                titleProp.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(titleProp.GetString()))
            {
                yield return element;
            }
        }
    }

    private static IEnumerable<JsonElement> WalkJsonTree(JsonElement element)
    {
        yield return element;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var child in WalkJsonTree(property.Value))
                        yield return child;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var child in WalkJsonTree(item))
                        yield return child;
                }
                break;
        }
    }

    private static void UpdateNowPlayingFromFirstStream(JsonElement streamsElement, IStreamCaptureHost host)
    {
        foreach (var stream in streamsElement.EnumerateArray())
        {
            if (!TryExtractArtistTrackFromStream(stream, out var trackName, out var artistName, out var stationName, out var albumName, out var durationMs, out var imageUrl))
                continue;

            host.MetadataTracker.UpdateFromArtistTrack(
                trackName,
                artistName ?? string.Empty,
                stationName: stationName,
                albumName: albumName,
                durationMs: durationMs,
                albumArtUrl: imageUrl,
                source: "tuneSource");

            Log.Information("Now playing from tuneSource: {Track} ({Artist}) on {Station}", trackName, artistName, stationName);
            break;
        }
    }

    private static bool TryExtractArtistTrackFromStream(
        JsonElement stream,
        out string trackName,
        out string? artistName,
        out string? stationName,
        out string? albumName,
        out long? durationMs,
        out string? imageUrl)
    {
        trackName = string.Empty;
        artistName = null;
        stationName = null;
        albumName = null;
        durationMs = null;
        imageUrl = null;

        if (!stream.TryGetProperty("metadata", out var metadata))
            return false;

        if (!metadata.TryGetProperty("artist", out var artist))
            return false;

        stationName = GetJsonString(artist, "stationName");

        if (!artist.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in items.EnumerateArray())
        {
            trackName = GetJsonString(item, "name") ?? string.Empty;
            artistName = GetJsonString(item, "artistName");
            albumName = GetJsonString(item, "albumName");
            durationMs = GetJsonInt64(item, "duration");
            imageUrl = ExtractPreferredImage(item, "images");
            return !string.IsNullOrWhiteSpace(trackName);
        }

        return false;
    }

    private static void ProcessChannelLinearTuneSource(JsonElement jsonElement, IStreamCaptureHost host)
    {
        var channelId = GetJsonString(jsonElement, "id");
        if (string.IsNullOrWhiteSpace(channelId))
            return;

        if (!jsonElement.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("live", out var live))
        {
            return;
        }

        var channelName = GetJsonString(live, "channelName") ?? "Live Channel";
        var channelNumber = live.TryGetProperty("channelNumber", out var channelNumberProp) &&
                            channelNumberProp.TryGetInt32(out var number)
            ? number
            : (int?)null;

        var showName = ResolveCurrentLiveShowName(live);

        host.LiveQueueTracker.SetActiveLiveChannel(channelId, channelName, channelNumber, showName);
        host.MetadataTracker.UpdateStationName(channelName);

        Log.Information("Live channel active: {Channel} (ch {Number}) show={Show}",
            channelName, channelNumber, showName ?? "(unknown)");
    }

    private static string? ResolveCurrentLiveShowName(JsonElement live)
    {
        if (!live.TryGetProperty("episodes", out var episodes) || episodes.ValueKind != JsonValueKind.Array)
            return null;

        var now = DateTime.UtcNow;
        string? fallback = null;

        foreach (var episode in episodes.EnumerateArray())
        {
            var name = GetJsonString(episode, "showName") ?? GetJsonString(episode, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            fallback ??= name;

            if (!episode.TryGetProperty("startTimestamp", out var startProp) ||
                startProp.ValueKind != JsonValueKind.String ||
                !DateTime.TryParse(startProp.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var startUtc))
            {
                continue;
            }

            var durationMs = GetJsonInt64(episode, "duration") ?? 0;
            var endUtc = durationMs > 0 ? startUtc.AddMilliseconds(durationMs) : startUtc.AddHours(5);

            if (startUtc <= now && now < endUtc)
                return name;
        }

        return fallback;
    }

    private static async Task HandleLookAroundAsync(string url, IStreamCaptureHost host)
    {
        try
        {
            host.LiveQueueTracker.LookAroundUrl = url;

            var body = await FetchLookAroundBodyAsync(url);
            if (string.IsNullOrWhiteSpace(body))
                return;

            var activeChannelId = host.LiveQueueTracker.ActiveChannel?.ChannelId;
            if (string.IsNullOrWhiteSpace(activeChannelId))
            {
                host.LiveQueueTracker.SetPendingLookAroundBody(body);
                Log.Debug("Buffered lookAround payload — waiting for active live channel");
                return;
            }

            await ProcessLookAroundBodyAsync(body, activeChannelId, host);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing lookAround response");
        }
    }

    private static async Task<string?> FetchLookAroundBodyAsync(string url)
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

        var response = await httpClient.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
        {
            Log.Warning("lookAround fetch failed: {StatusCode}", response.StatusCode);
            return null;
        }

        Directory.CreateDirectory("Stations");
        await File.WriteAllTextAsync("Stations/lookaround.json", body);
        return body;
    }

    private static async Task TryProcessPendingLookAroundAsync(IStreamCaptureHost host)
    {
        var pending = host.LiveQueueTracker.TakePendingLookAroundBody();
        var activeChannelId = host.LiveQueueTracker.ActiveChannel?.ChannelId;
        if (string.IsNullOrWhiteSpace(pending) || string.IsNullOrWhiteSpace(activeChannelId))
            return;

        await ProcessLookAroundBodyAsync(pending, activeChannelId, host);
    }

    private static async Task ProcessLookAroundBodyAsync(string body, string activeChannelId, IStreamCaptureHost host)
    {
        var root = JsonSerializer.Deserialize<JsonElement>(body);
        if (root.TryGetProperty("delta", out var deltaProp))
        {
            var delta = deltaProp.GetString();
            if (!string.IsNullOrWhiteSpace(delta))
            {
                host.LiveQueueTracker.LookAroundUrl =
                    $"https://lookaround-cache-prod.streaming.siriusxm.com/playbackservices/v1/live/lookAround?delta={Uri.EscapeDataString(delta)}";
            }
        }

        var channelMap = GetLookAroundChannelMap(root);

        foreach (var channelProperty in channelMap.EnumerateObject())
        {
            if (channelProperty.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (!string.Equals(channelProperty.Name, activeChannelId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!channelProperty.Value.TryGetProperty("cuts", out var cutsElement) ||
                cutsElement.ValueKind != JsonValueKind.Array)
            {
                Log.Debug("lookAround channel {ChannelId} has no cuts array", activeChannelId);
                return;
            }

            var showName = ExtractLookAroundShowName(channelProperty.Value);
            var cuts = ParseLookAroundCuts(cutsElement);

            await host.RunOnUiAsync(() =>
            {
                host.LiveQueueTracker.MergeLookAroundCuts(activeChannelId, cuts, showName);

                var nowPlaying = host.LiveQueueTracker.GetNowPlayingCut()
                    ?? cuts.Where(c => !c.IsAd).OrderByDescending(c => c.ValidFromUtc).FirstOrDefault();

                if (nowPlaying != null)
                {
                    host.MetadataTracker.UpdateFromArtistTrack(
                        nowPlaying.TrackName,
                        nowPlaying.ArtistName,
                        stationName: host.LiveQueueTracker.ActiveChannel?.ChannelName,
                        albumArtUrl: nowPlaying.ImageUrl,
                        source: "lookAround");
                }
            });

            Log.Information("lookAround merged {Count} cut(s) for {Channel} (up next: {UpNext})",
                cuts.Count, activeChannelId, host.LiveQueueTracker.UpNextCount);
            return;
        }

        Log.Debug("Active channel {ChannelId} not found in lookAround payload", activeChannelId);
    }

    private static JsonElement GetLookAroundChannelMap(JsonElement root)
    {
        if (root.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Object)
            return channels;

        return root;
    }

    private static void TryActivateLiveChannelFromRequest(string? payload, IStreamCaptureHost host)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return;

        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(payload);
            if (!string.Equals(GetJsonString(json, "type"), "channel-linear", StringComparison.OrdinalIgnoreCase))
                return;

            var channelId = GetJsonString(json, "id");
            if (string.IsNullOrWhiteSpace(channelId))
                return;

            host.LiveQueueTracker.SetActiveLiveChannel(channelId, "Live Channel", null, null);
            Log.Information("Live channel detected from tuneSource request: {ChannelId}", channelId);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not parse tuneSource request for live channel activation");
        }
    }

    private static string? ExtractLookAroundShowName(JsonElement channelElement)
    {
        if (!channelElement.TryGetProperty("shows", out var shows) || shows.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var show in shows.EnumerateArray())
        {
            var name = GetJsonString(show, "name");
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return null;
    }

    private static List<LiveCutEntry> ParseLookAroundCuts(JsonElement cutsElement)
    {
        var cuts = new List<LiveCutEntry>();

        foreach (var cut in cutsElement.EnumerateArray())
        {
            var trackName = GetJsonString(cut, "name");
            if (string.IsNullOrWhiteSpace(trackName))
                continue;

            var artistName = GetJsonString(cut, "artistName") ?? "Unknown";
            var isAd = cut.TryGetProperty("isAd", out var isAdProp) && isAdProp.ValueKind == JsonValueKind.True;
            var validFromUtc = ParseUtcTimestamp(GetJsonString(cut, "validFrom")) ?? DateTime.UtcNow;
            string? imageUrl = null;

            if (cut.TryGetProperty("image", out var image) &&
                image.TryGetProperty("url", out var imageUrlProp))
            {
                imageUrl = StreamEntry.DecodeImageUrl(imageUrlProp.GetString());
            }

            cuts.Add(new LiveCutEntry
            {
                TrackName = trackName,
                ArtistName = artistName,
                ValidFromUtc = validFromUtc,
                IsAd = isAd,
                ImageUrl = imageUrl
            });
        }

        return cuts;
    }

    private static DateTime? ParseUtcTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    public Task RefreshLiveLookAroundAsync(IStreamCaptureHost host)
    {
        if (!host.IsMonitoring || !host.LiveQueueTracker.IsLiveActive)
            return Task.CompletedTask;

        var url = host.LiveQueueTracker.LookAroundUrl;
        if (string.IsNullOrWhiteSpace(url))
            return Task.CompletedTask;

        return HandleLookAroundAsync(url, host);
    }
}
