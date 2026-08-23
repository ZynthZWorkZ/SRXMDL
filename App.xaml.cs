using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace SRXMDL;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (TryGetArgValue(e.Args, "--clear-cache-after-pid", out var pidValue) &&
            int.TryParse(pidValue, out var previousPid))
        {
            WaitForProcessExit(previousPid, TimeSpan.FromSeconds(15));

            if (TryGetArgValue(e.Args, "--clear-cache-browser-pid", out var browserPidValue) &&
                int.TryParse(browserPidValue, out var browserPid))
            {
                WaitForProcessExit(browserPid, TimeSpan.FromSeconds(15));
            }

            DeleteWebView2DataFolder();
        }

        base.OnStartup(e);
    }

    private static bool TryGetArgValue(string[] args, string key, out string value)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            {
                value = args[i + 1];
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static void WaitForProcessExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // Process has already exited.
        }
        catch (InvalidOperationException)
        {
            // Process has already exited.
        }
    }

    private static void DeleteWebView2DataFolder()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "WebView2Data");

        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(400);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(400);
            }
        }
    }
}
