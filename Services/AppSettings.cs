using System.IO;
using System.Text.Json;

namespace SRXMDL.Services;

public static class AppSettings
{
    private const string DefaultFolderName = "Captures";
    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static SettingsModel _model = Load();

    public static string GetDownloadDirectory()
    {
        var dir = string.IsNullOrWhiteSpace(_model.DownloadDirectory)
            ? GetDefaultDownloadDirectory()
            : _model.DownloadDirectory.Trim();

        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetDefaultDownloadDirectory() =>
        Path.Combine(AppContext.BaseDirectory, DefaultFolderName);

    public static string GetDownloadPath(string fileName) =>
        Path.Combine(GetDownloadDirectory(), fileName);

    public static void SetDownloadDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        _model.DownloadDirectory = Path.GetFullPath(path.Trim());
        Save();
    }

    public static void ResetDownloadDirectory()
    {
        _model.DownloadDirectory = null;
        Save();
    }

    public static int GetStreamServerPort() =>
        _model.StreamServerPort is > 0 and < 65536 ? _model.StreamServerPort.Value : 8765;

    public static void SetStreamServerPort(int port)
    {
        if (port is <= 0 or >= 65536)
            return;

        _model.StreamServerPort = port;
        Save();
    }

    private static SettingsModel Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new SettingsModel();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<SettingsModel>(json) ?? new SettingsModel();
        }
        catch
        {
            return new SettingsModel();
        }
    }

    private static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_model, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // ignore persistence errors
        }
    }

    private sealed class SettingsModel
    {
        public string? DownloadDirectory { get; set; }
        public int? StreamServerPort { get; set; }
    }
}
