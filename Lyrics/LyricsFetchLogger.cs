using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SRXMDL.Lyrics;

/// <summary>
/// Appends detailed lyrics-fetch diagnostics to <c>Lyrics/lyrics_fetch.log</c>
/// so search attempts and failures can be reviewed when improving LyricsFetch.exe.
/// </summary>
internal static class LyricsFetchLogger
{
    private static readonly object WriteLock = new();

    public static string LyricsDirectory => ResolveLyricsDirectory();

    public static string LogFilePath => Path.Combine(LyricsDirectory, "lyrics_fetch.log");

    public static void LogNoTrack()
    {
        AppendBlock(new StringBuilder()
            .AppendLine("EVENT       No track playing")
            .AppendLine("RESULT      Skipped — nothing to search"));
    }

    public static void LogFetchStarted(
        string trackName,
        string artistName,
        string artistSource,
        string stationName,
        string searchQuery,
        string querySource,
        string exePath,
        string arguments,
        string workingDirectory,
        string outputPathCwd,
        string outputPathExeDir)
    {
        AppendBlock(new StringBuilder()
            .AppendLine("EVENT       Fetch started")
            .AppendLine($"TRACK       {trackName}")
            .AppendLine($"ARTIST      {FormatOptional(artistName)}")
            .AppendLine($"ARTIST_FROM {artistSource}")
            .AppendLine($"STATION     {stationName}")
            .AppendLine($"SEARCH      {searchQuery}")
            .AppendLine($"QUERY_FROM  {querySource}")
            .AppendLine($"EXE         {exePath}")
            .AppendLine($"ARGS        {arguments}")
            .AppendLine($"WORKDIR     {workingDirectory}")
            .AppendLine($"OUTPUT      {outputPathCwd}")
            .AppendLine($"OUTPUT_ALT  {outputPathExeDir}"));
    }

    public static void LogExeNotFound(string workingDirectory, string expectedExePath)
    {
        AppendBlock(new StringBuilder()
            .AppendLine("EVENT       Fetch aborted")
            .AppendLine("RESULT      LyricsFetch.exe not found")
            .AppendLine($"WORKDIR     {workingDirectory}")
            .AppendLine($"EXPECTED    {expectedExePath}")
            .AppendLine("CHECKED     BaseDirectory/Lyrics, CWD/Lyrics, CWD"));
    }

    public static void LogProcessStartFailed(string trackName, string searchQuery, string exePath, Exception ex)
    {
        AppendBlock(new StringBuilder()
            .AppendLine("EVENT       Process start failed")
            .AppendLine($"TRACK       {trackName}")
            .AppendLine($"SEARCH      {searchQuery}")
            .AppendLine($"EXE         {exePath}")
            .AppendLine($"ERROR       {ex.GetType().Name}: {ex.Message}"));
    }

    public static void LogFetchSucceeded(
        string trackName,
        string artistName,
        string stationName,
        string searchQuery,
        int exitCode,
        int waitedSeconds,
        string stdout,
        string stderr,
        string lyricsPath,
        int characterCount)
    {
        var body = new StringBuilder()
            .AppendLine("EVENT       Fetch succeeded")
            .AppendLine($"TRACK       {trackName}")
            .AppendLine($"ARTIST      {FormatOptional(artistName)}")
            .AppendLine($"STATION     {stationName}")
            .AppendLine($"SEARCH      {searchQuery}")
            .AppendLine($"EXIT_CODE   {exitCode}")
            .AppendLine($"WAITED      {waitedSeconds}s")
            .AppendLine($"LYRICS_FILE {lyricsPath}")
            .AppendLine($"CHAR_COUNT  {characterCount}")
            .AppendLine("RESULT      Lyrics loaded");

        AppendProcessOutput(body, stdout, stderr);
        AppendSearchAttempts(body, stdout);
        AppendBlock(body);
    }

    public static void LogFetchFailed(
        string trackName,
        string artistName,
        string stationName,
        string searchQuery,
        int exitCode,
        int waitedSeconds,
        string stdout,
        string stderr,
        string outputPathCwd,
        string outputPathExeDir,
        string? reason = null)
    {
        var body = new StringBuilder()
            .AppendLine("EVENT       Fetch failed")
            .AppendLine($"TRACK       {trackName}")
            .AppendLine($"ARTIST      {FormatOptional(artistName)}")
            .AppendLine($"STATION     {stationName}")
            .AppendLine($"SEARCH      {searchQuery}")
            .AppendLine($"EXIT_CODE   {exitCode}")
            .AppendLine($"WAITED      {waitedSeconds}s")
            .AppendLine($"OUTPUT      {outputPathCwd}")
            .AppendLine($"OUTPUT_ALT  {outputPathExeDir}")
            .AppendLine($"RESULT      {reason ?? "No lyrics.txt produced"}");

        AppendProcessOutput(body, stdout, stderr);
        AppendSearchAttempts(body, stdout);
        AppendBlock(body);
    }

    public static void LogEmptyLyricsFile(
        string trackName,
        string artistName,
        string stationName,
        string searchQuery,
        string lyricsPath,
        int exitCode,
        string stdout,
        string stderr)
    {
        var body = new StringBuilder()
            .AppendLine("EVENT       Empty lyrics file")
            .AppendLine($"TRACK       {trackName}")
            .AppendLine($"ARTIST      {FormatOptional(artistName)}")
            .AppendLine($"STATION     {stationName}")
            .AppendLine($"SEARCH      {searchQuery}")
            .AppendLine($"EXIT_CODE   {exitCode}")
            .AppendLine($"LYRICS_FILE {lyricsPath}")
            .AppendLine("RESULT      Output file existed but was empty");

        AppendProcessOutput(body, stdout, stderr);
        AppendSearchAttempts(body, stdout);
        AppendBlock(body);
    }

    public static void LogDiscardedStaleResult(string trackName, string searchQuery)
    {
        AppendBlock(new StringBuilder()
            .AppendLine("EVENT       Result discarded")
            .AppendLine($"TRACK       {trackName}")
            .AppendLine($"SEARCH      {searchQuery}")
            .AppendLine("RESULT      Stale — now playing changed before fetch finished"));
    }

    public static void LogUnexpectedError(string? trackName, string? searchQuery, Exception ex)
    {
        AppendBlock(new StringBuilder()
            .AppendLine("EVENT       Unexpected error")
            .AppendLine($"TRACK       {trackName ?? "(unknown)"}")
            .AppendLine($"SEARCH      {searchQuery ?? "(unknown)"}")
            .AppendLine($"ERROR       {ex.GetType().Name}: {ex.Message}")
            .AppendLine(ex.StackTrace ?? string.Empty));
    }

    private static string FormatOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(unknown)" : value.Trim();

    private static void AppendProcessOutput(StringBuilder body, string stdout, string stderr)
    {
        body.AppendLine();
        body.AppendLine("--- stdout ---");
        body.AppendLine(string.IsNullOrWhiteSpace(stdout) ? "(empty)" : stdout.TrimEnd());
        body.AppendLine("--- stderr ---");
        body.AppendLine(string.IsNullOrWhiteSpace(stderr) ? "(empty)" : stderr.TrimEnd());
    }

    /// <summary>
    /// Pulls search strings from LyricsFetch.exe console output for quick scanning in the log.
    /// </summary>
    private static void AppendSearchAttempts(StringBuilder body, string stdout)
    {
        var attempts = ExtractSearchAttempts(stdout);
        body.AppendLine();
        body.AppendLine("--- search attempts ---");

        if (attempts.Count == 0)
        {
            body.AppendLine("(none parsed from stdout — check raw output above)");
            return;
        }

        for (var i = 0; i < attempts.Count; i++)
            body.AppendLine($"  {i + 1}. {attempts[i]}");
    }

    internal static List<string> ExtractSearchAttempts(string? stdout)
    {
        var attempts = new List<string>();
        if (string.IsNullOrWhiteSpace(stdout))
            return attempts;

        foreach (var rawLine in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var match = Regex.Match(line,
                @"^(?:Searching AZLyrics for:|Trying search:)\s*(.+)$",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var query = match.Groups[1].Value.Trim().Trim('"');
                if (query.Length > 0 && !attempts.Contains(query, StringComparer.OrdinalIgnoreCase))
                    attempts.Add(query);
            }
        }

        return attempts;
    }

    private static void AppendBlock(StringBuilder content)
    {
        var entry = new StringBuilder()
            .AppendLine(new string('=', 80))
            .AppendLine($"TIME        {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .Append(content.ToString().TrimEnd())
            .AppendLine()
            .AppendLine();

        lock (WriteLock)
        {
            Directory.CreateDirectory(LyricsDirectory);
            File.AppendAllText(LogFilePath, entry.ToString(), Encoding.UTF8);
        }
    }

    private static string ResolveLyricsDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Lyrics"),
            Path.Combine(Environment.CurrentDirectory, "Lyrics"),
            Path.Combine(AppContext.BaseDirectory, "..", "Lyrics")
        };

        foreach (var path in candidates)
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full))
                return full;
        }

        return Path.GetFullPath(candidates[0]);
    }
}
