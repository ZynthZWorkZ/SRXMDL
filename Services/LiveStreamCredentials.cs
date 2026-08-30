using System.IO;
using System.Text.Json;

namespace SRXMDL.Services;

internal static class LiveStreamCredentials
{
    public static async Task<string?> LoadBearerAsync()
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

        if (!File.Exists(tuneSource))
            return null;

        var tuneContent = (await File.ReadAllTextAsync(tuneSource)).Trim();
        if (string.IsNullOrWhiteSpace(tuneContent))
            return null;

        return tuneContent.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? tuneContent
            : $"Bearer {tuneContent}";
    }

    public static async Task<byte[]?> LoadHlsKeyBytesAsync()
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

    public static async Task ReloadAsync(StreamProxyServer server)
    {
        server.Bearer = await LoadBearerAsync();
        server.DefaultKeyBytes = await LoadHlsKeyBytesAsync();
    }

    private static IEnumerable<string> GetCredentialPaths(string fileName)
    {
        yield return Path.Combine(Directory.GetCurrentDirectory(), "HLSKey", fileName);
        yield return Path.Combine(AppContext.BaseDirectory, "HLSKey", fileName);
        yield return Path.Combine(Directory.GetCurrentDirectory(), "Login", fileName);
        yield return Path.Combine(AppContext.BaseDirectory, "Login", fileName);
    }
}
