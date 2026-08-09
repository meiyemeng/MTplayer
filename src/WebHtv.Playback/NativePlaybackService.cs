using LibVLCSharp.Shared;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using WebHtv.Core.Catalogue;

namespace WebHtv.Playback;

public sealed class NativePlaybackService : IDisposable
{
    private readonly LibVLC _libVlc;

    public NativePlaybackService(bool hardwareDecode = true)
    {
        var nativeDirectory = ResolveLibVlcDirectory();
        if (nativeDirectory is null)
        {
            throw new VLCException(
                $"未找到 LibVLC 播放器组件。程序目录：{AppContext.BaseDirectory}");
        }

        LibVLCSharp.Shared.Core.Initialize(nativeDirectory);
        _libVlc = new LibVLC(
            "--network-caching=3000",
            "--file-caching=1500",
            hardwareDecode ? "--avcodec-hw=any" : "--avcodec-hw=none");
        Player = new MediaPlayer(_libVlc);
    }

    public static string? ResolveLibVlcDirectory()
    {
        var architectureFolder = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => null
        };
        if (architectureFolder is null) return null;

        foreach (var root in GetNativeSearchRoots())
        {
            var candidates = new[]
            {
                Path.Combine(root, "libvlc", architectureFolder),
                Path.Combine(root, architectureFolder),
                root
            };
            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(candidate, "libvlc.dll")) &&
                    File.Exists(Path.Combine(candidate, "libvlccore.dll")) &&
                    Directory.Exists(Path.Combine(candidate, "plugins")))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetNativeSearchRoots()
    {
        yield return AppContext.BaseDirectory;

        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is not string searchDirectories) yield break;
        foreach (var directory in searchDirectories.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Directory.Exists(directory)) yield return directory;
        }
    }

    public MediaPlayer Player { get; }

    public Task OpenAsync(PlayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        using var media = new Media(_libVlc, new Uri(request.Url));
        foreach (var header in request.Headers)
        {
            var option = header.Key.ToUpperInvariant() switch
            {
                "USER-AGENT" => $":http-user-agent={header.Value}",
                "REFERER" => $":http-referrer={header.Value}",
                "COOKIE" => $":http-cookie={header.Value}",
                _ => $":http-header={header.Key}: {header.Value}"
            };
            media.AddOption(option);
        }

        Player.Play(media);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Player.Dispose();
        _libVlc.Dispose();
    }
}
