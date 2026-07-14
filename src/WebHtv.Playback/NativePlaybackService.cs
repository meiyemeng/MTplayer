using LibVLCSharp.Shared;
using WebHtv.Core.Catalogue;

namespace WebHtv.Playback;

public sealed class NativePlaybackService : IDisposable
{
    private readonly LibVLC _libVlc;

    public NativePlaybackService(bool hardwareDecode = true)
    {
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC(
            "--network-caching=3000",
            "--file-caching=1500",
            hardwareDecode ? "--avcodec-hw=any" : "--avcodec-hw=none");
        Player = new MediaPlayer(_libVlc);
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
