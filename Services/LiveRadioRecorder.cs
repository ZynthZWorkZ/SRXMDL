using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SRXMDL.Services;

public sealed class LiveRadioRecorder : IDisposable
{
    private Process? _process;
    private HlsLiveProxy? _proxy;

    public bool IsRecording => _process is { HasExited: false };

    public string? OutputFilePath { get; private set; }

    public async Task<(bool Ok, string Message)> StartAsync(string m3u8Url, string outputBaseName)
    {
        if (IsRecording)
            return (false, "Already recording.");

        var bearer = await LoadBearerAsync();
        var keyBytes = await LoadHlsKeyBytesAsync();
        if (string.IsNullOrWhiteSpace(bearer) || keyBytes == null || keyBytes.Length == 0)
            return (false, "Missing HLS key or Authorization bearer. Play live radio briefly so credentials are captured.");

        var safeName = SanitizeFileName(outputBaseName);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "live-radio";

        OutputFilePath = Path.GetFullPath($"{safeName}.ts");

        try
        {
            _proxy = new HlsLiveProxy(m3u8Url, bearer, keyBytes);
            _proxy.Start();

            var command = BuildFfmpegCommand(_proxy.PlaylistUrl, bearer, OutputFilePath);
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                UseShellExecute = false,
                CreateNoWindow = false
            });

            if (_process == null)
            {
                _proxy.Dispose();
                _proxy = null;
                return (false, "Failed to start ffmpeg.");
            }

            return (true, OutputFilePath);
        }
        catch (Exception ex)
        {
            _proxy?.Dispose();
            _proxy = null;
            _process = null;
            OutputFilePath = null;
            return (false, ex.Message);
        }
    }

    public (bool Ok, string Message) Stop()
    {
        if (!IsRecording)
            return (false, "Not recording.");

        var outputPath = OutputFilePath;
        try
        {
            _process!.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            _process = null;
            _proxy?.Dispose();
            _proxy = null;
        }

        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
            return (false, "Recording stopped, but no output file was found.");

        var sizeKb = new FileInfo(outputPath).Length / 1024;
        return (true, $"{outputPath} ({sizeKb:N0} KB)");
    }

    public void Dispose()
    {
        if (IsRecording)
            Stop();
        else
            _proxy?.Dispose();
    }

    public static bool IsVodM3u8(string url) =>
        url.Contains("aod-", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("vod-", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("/EPISODE_", StringComparison.OrdinalIgnoreCase);

    private static string BuildFfmpegCommand(string playlistUrl, string bearer, string outputFile)
    {
        var headers = new StringBuilder()
            .Append("Authorization: ").Append(bearer).Append("\\r\\n")
            .Append("Origin: https://www.siriusxm.com\\r\\n")
            .Append("Referer: https://www.siriusxm.com\\r\\n")
            .Append("User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36\\r\\n")
            .ToString();

        return "ffmpeg -hide_banner -loglevel warning " +
               "-protocol_whitelist file,http,https,tcp,tls,crypto " +
               $"-headers \"{headers}\" " +
               $"-i \"{playlistUrl}\" " +
               "-c copy -f mpegts -y " +
               $"\"{outputFile}\"";
    }

    private static string SanitizeFileName(string name)
    {
        var trimmed = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(c, '_');
        return trimmed;
    }

    private static async Task<string?> LoadBearerAsync()
    {
        foreach (var path in GetCredentialPaths("authorization Bearer.txt"))
        {
            if (!File.Exists(path))
                continue;

            var content = (await File.ReadAllTextAsync(path)).Trim();
            if (!string.IsNullOrWhiteSpace(content))
                return content;
        }

        var tuneSource = Path.Combine(Directory.GetCurrentDirectory(), "Stations", "tunesource.txt");
        if (!File.Exists(tuneSource))
            tuneSource = Path.Combine(AppContext.BaseDirectory, "Stations", "tunesource.txt");

        if (File.Exists(tuneSource))
        {
            var content = (await File.ReadAllTextAsync(tuneSource)).Trim();
            if (!string.IsNullOrWhiteSpace(content))
                return content.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? content
                    : $"Bearer {content}";
        }

        return null;
    }

    private static async Task<byte[]?> LoadHlsKeyBytesAsync()
    {
        foreach (var path in GetCredentialPaths("response.json"))
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("key", out var keyProp))
                    continue;

                var keyString = keyProp.GetString();
                if (string.IsNullOrWhiteSpace(keyString))
                    continue;

                return Convert.FromBase64String(keyString.Trim());
            }
            catch
            {
                // try next path
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCredentialPaths(string fileName)
    {
        yield return Path.Combine(Directory.GetCurrentDirectory(), "HLSKey", fileName);
        yield return Path.Combine(AppContext.BaseDirectory, "HLSKey", fileName);
        yield return Path.Combine(Directory.GetCurrentDirectory(), "Login", fileName);
        yield return Path.Combine(AppContext.BaseDirectory, "Login", fileName);
    }
}
