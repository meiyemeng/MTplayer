using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WebHtv.Playback;

namespace WebHtv.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--verify-playback-runtime", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var playback = new NativePlaybackService();
                Shutdown(0);
            }
            catch
            {
                Shutdown(2);
            }
            return;
        }

        if (e.Args.Contains("--verify-seek-calculation", StringComparer.OrdinalIgnoreCase))
        {
            var passed =
                PlaybackTimeline.MapPointerToPosition(0, 100, 120_000) == 0 &&
                PlaybackTimeline.MapPointerToPosition(50, 100, 120_000) == 60_000 &&
                PlaybackTimeline.MapPointerToPosition(125, 100, 120_000) == 120_000;
            Shutdown(passed ? 0 : 3);
            return;
        }

        if (e.Args.Contains("--verify-live-channel-organization", StringComparer.OrdinalIgnoreCase))
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var channels = new[]
            {
                new LiveChannel("卫视频道", "湖南卫视", "https://live-a.example/hunan.m3u8", headers),
                new LiveChannel("备用线路", "湖南卫视 HD", "https://live-b.example/hunan.m3u8", headers),
                new LiveChannel("央视频道", "CCTV5", "https://live.example/cctv5.m3u8", headers)
            };
            var groups = LiveChannelOrganizer.GroupChannels(channels);
            var hunan = groups.SingleOrDefault(group => group.Name == "湖南卫视");
            var passed =
                groups.Count == 2 &&
                hunan?.Sources.Count == 2 &&
                hunan.Category == "卫视频道" &&
                LiveChannelOrganizer.Matches(hunan, "湖南", LiveChannelOrganizer.AllCategory) &&
                !LiveChannelOrganizer.Matches(hunan, "湖南", "央视频道");
            Shutdown(passed ? 0 : 4);
            return;
        }

        // Normal startup: install global safety nets so that an unhandled exception
        // thrown inside an async void event handler (search, navigation, poster click,
        // live errors, …) no longer terminates the process with an immediate crash.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception, "UI");
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException(e.Exception, "Task");
        e.SetObserved();
    }

    private static void LogException(Exception? exception, string source)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MTPlayer");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "crash.log");
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Never throw from inside an exception handler.
        }
    }
}
