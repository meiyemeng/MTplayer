namespace MTPlayer.Server.Auth;

public sealed class AuthTiming(TimeProvider timeProvider, TimeSpan minimumDuration)
{
    public long GetTimestamp() => timeProvider.GetTimestamp();

    public async Task CompleteAsync(long startedAt, CancellationToken cancellationToken)
    {
        var remaining = minimumDuration - timeProvider.GetElapsedTime(startedAt);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, timeProvider, cancellationToken);
        }
    }
}
