using System.IO;
using System.Text;

namespace SRXMDL.Login;

public static class CookieFileHelper
{
    public const string CookieFileName = "cookies-siriusxm-com.txt";

    public static string CookieFilePath =>
        Path.Combine(AppContext.BaseDirectory, CookieFileName);

    public static string ToNetscapeFormat(IEnumerable<CookieExportEntry> cookies)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Netscape HTTP Cookie File");
        sb.AppendLine();

        var unique = cookies
            .GroupBy(c => new { c.Domain, c.Path, c.Name })
            .Select(g => g.First())
            .OrderBy(c => c.Domain)
            .ThenBy(c => c.Path)
            .ThenBy(c => c.Name);

        foreach (var cookie in unique)
        {
            var domainFlag = cookie.Domain.StartsWith('.') ? "TRUE" : "FALSE";
            var secureFlag = cookie.IsSecure ? "TRUE" : "FALSE";
            var expiration = 0L;

            if (cookie.Expires.HasValue)
            {
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                expiration = (long)(cookie.Expires.Value.ToUniversalTime() - epoch).TotalSeconds;
            }

            sb.AppendLine($"{cookie.Domain}\t{domainFlag}\t{cookie.Path}\t{secureFlag}\t{expiration}\t{cookie.Name}\t{cookie.Value}");
        }

        return sb.ToString();
    }

    public static async Task SaveAsync(IEnumerable<CookieExportEntry> cookies, CancellationToken cancellationToken = default)
    {
        var content = ToNetscapeFormat(cookies);
        await File.WriteAllTextAsync(CookieFilePath, content, cancellationToken);
    }

    public static bool Exists() => File.Exists(CookieFilePath);
}

public sealed class CookieExportEntry
{
    public string Domain { get; init; } = string.Empty;
    public string Path { get; init; } = "/";
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public DateTime? Expires { get; init; }
    public bool IsSecure { get; init; }
}
