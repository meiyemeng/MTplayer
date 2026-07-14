using MTPlayer.Server.Security;

namespace MTPlayer.Server.Auth;

public sealed class Argon2PasswordService : IDisposable
{
    private readonly PasswordHasher _passwordHasher;
    private readonly SemaphoreSlim _gate;
    private readonly string _dummyHash;
    private int _activeCount;
    private int _peakObservedConcurrency;

    public Argon2PasswordService(PasswordHasher passwordHasher, int maximumConcurrency)
    {
        _passwordHasher = passwordHasher;
        MaximumConcurrency = Math.Clamp(maximumConcurrency, 1, 4);
        _gate = new SemaphoreSlim(MaximumConcurrency, MaximumConcurrency);
        _dummyHash = passwordHasher.HashPassword("MTPlayer-Dummy-Password-2026");
    }

    public int MaximumConcurrency { get; }

    public int PeakObservedConcurrency => Volatile.Read(ref _peakObservedConcurrency);

    public Task<string> HashAsync(string password, CancellationToken cancellationToken)
    {
        ValidatePassword(password);
        return ExecuteAsync(() => _passwordHasher.HashPassword(password), cancellationToken);
    }

    public Task<bool> VerifyOrDummyAsync(
        string? encodedHash,
        string password,
        CancellationToken cancellationToken)
    {
        ValidatePassword(password);
        var hash = string.IsNullOrEmpty(encodedHash) ? _dummyHash : encodedHash;
        return ExecuteAsync(() => _passwordHasher.VerifyPassword(hash, password), cancellationToken);
    }

    internal void ResetPeakConcurrencyForTests() =>
        Volatile.Write(ref _peakObservedConcurrency, Volatile.Read(ref _activeCount));

    public void Dispose() => _gate.Dispose();

    private async Task<T> ExecuteAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        var active = Interlocked.Increment(ref _activeCount);
        UpdatePeak(active);
        try
        {
            return await Task.Run(operation, CancellationToken.None);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCount);
            _gate.Release();
        }
    }

    private void UpdatePeak(int active)
    {
        var observed = Volatile.Read(ref _peakObservedConcurrency);
        while (active > observed)
        {
            var original = Interlocked.CompareExchange(ref _peakObservedConcurrency, active, observed);
            if (original == observed)
            {
                return;
            }

            observed = original;
        }
    }

    private static void ValidatePassword(string? password)
    {
        if (password is null || password.Length is < 10 or > 128)
        {
            throw new ArgumentException("Password must be between 10 and 128 characters.", nameof(password));
        }
    }
}
