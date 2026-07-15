using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using MTPlayer.Client.Core.Account;

namespace WebHtv.Desktop.Security;

internal sealed class DpapiTokenStore(string filePath) : ITokenStore, IDisposable
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MTPlayer.TokenStore.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath = Path.GetFullPath(filePath);

    public async Task<string?> ReadRefreshTokenAsync(
        ServerBinding binding,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            byte[]? plaintext = null;
            try
            {
                var protectedBytes = await File.ReadAllBytesAsync(_filePath, cancellationToken);
                plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                var envelope = JsonSerializer.Deserialize<TokenEnvelope>(plaintext, JsonOptions);
                return envelope is not null &&
                    string.Equals(envelope.ServerOrigin, binding.ToString(), StringComparison.OrdinalIgnoreCase)
                        ? envelope.RefreshToken
                        : null;
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException or IOException)
            {
                return null;
            }
            finally
            {
                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteRefreshTokenAsync(
        ServerBinding binding,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 4096)
        {
            throw new ArgumentException("Refresh token is invalid.", nameof(refreshToken));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_filePath) ??
                throw new InvalidOperationException("Token file must have a directory.");
            Directory.CreateDirectory(directory);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(
                new TokenEnvelope(binding.ToString(), refreshToken),
                JsonOptions);
            byte[]? protectedBytes = null;
            var temporary = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
                await using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(protectedBytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, _filePath, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                if (protectedBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(protectedBytes);
                }

                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record TokenEnvelope(string ServerOrigin, string RefreshToken)
    {
        public override string ToString() => $"{nameof(TokenEnvelope)} {{ ServerOrigin = {ServerOrigin}, RefreshToken = [隐藏] }}";
    }
}
