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

        if (url.Contains("api.edge-gateway.siriusxm.com/playback/play/v1/peek"))
        {
            await HandlePeekAsync(url, request, host);
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
                        Log.Information("Channel Linear detected in playlist: {Id}",
                            jsonElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : "unknown");
                        break;
                }
            }

            if (jsonElement.TryGetProperty("streams", out var streamsElement) &&
                streamsElement.ValueKind == JsonValueKind.Array)
            {
                await ProcessTuneSourceStreamsAsync(streamsElement, host);
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
        url.Contains("imgsrv-sxm") ||
        url.Contains("lookaround-cache-prod.streaming.siriusxm.com");

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
}
