namespace WebHtv.Playback;

public static class PlaybackTimeline
{
    public static long MapPointerToPosition(double offset, double extent, long durationMs)
    {
        if (durationMs <= 0 || extent <= 0 || !double.IsFinite(offset) || !double.IsFinite(extent))
        {
            return 0;
        }

        var ratio = Math.Clamp(offset / extent, 0, 1);
        return (long)Math.Round(ratio * durationMs, MidpointRounding.AwayFromZero);
    }
}
