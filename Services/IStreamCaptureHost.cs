using System.Collections.ObjectModel;
using System.Windows.Threading;
using SRXMDL.Models;

namespace SRXMDL.Services;

public interface IStreamCaptureHost
{
    Dispatcher UiDispatcher { get; }
    ObservableCollection<StreamEntry> StreamEntries { get; }
    bool IsMonitoring { get; }
    void UpdateTotalCapturedCount();
    void RefreshStreamList();
    void SetStatus(string message);
    bool IsDuplicateStream(string url, out StreamEntry? existingEntry);
    Task ProcessArtistStationUrlAsync(string url);
    Task RunOnUiAsync(Action action);
    string? LastTuneSourceUrl { get; set; }
    string? LastTuneSourcePayload { get; set; }
    string? LastTuneSourceAuthToken { get; set; }
    bool CaptureBearer { get; set; }
    Func<string, Task>? AttemptAutoLoginWithCredsAsync { get; set; }
    PlaybackMetadataTracker MetadataTracker { get; }
    LiveQueueTracker LiveQueueTracker { get; }
    void SetLiveStreamUrl(string url);
}
