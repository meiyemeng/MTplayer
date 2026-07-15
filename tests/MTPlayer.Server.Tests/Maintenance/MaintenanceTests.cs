using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MTPlayer.Server.Data;
using MTPlayer.Server.Maintenance;
using MTPlayer.Server.Security;
using MTPlayer.Server.Tests.Auth;
using Xunit;

namespace MTPlayer.Server.Tests.Maintenance;

public sealed class MaintenanceTests(PostgreSqlAuthFixture fixture) : IClassFixture<PostgreSqlAuthFixture>
{
    [DockerFact]
    public async Task Failed_key_rotation_leaves_old_ciphertext_readable()
    {
        var prefix = $"rotation-{Guid.NewGuid():N}";
        try
        {
            var current = fixture.Factory.Services.GetRequiredService<ISecretProtector>();
            await InsertAsync($"{prefix}-a", current.Protect("first-secret"));
            await InsertAsync($"{prefix}-z", "invalid-ciphertext");
            var original = await ReadValueAsync($"{prefix}-a");
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var rotation = scope.ServiceProvider.GetRequiredService<KeyRotationService>();

            await Assert.ThrowsAsync<CryptographicException>(() =>
                rotation.RotateAsync(KeyRotationService.CreateKey()));
            var stored = await ReadValueAsync($"{prefix}-a");
            Assert.Equal(original, stored);
            Assert.Equal("first-secret", current.Unprotect(stored!));
        }
        finally
        {
            await DeleteAsync(prefix);
        }
    }

    [DockerFact]
    public async Task Successful_rotation_reencrypts_every_value_and_invalid_new_key_changes_nothing()
    {
        var prefix = $"rotation-{Guid.NewGuid():N}";
        try
        {
            var current = fixture.Factory.Services.GetRequiredService<ISecretProtector>();
            await InsertAsync($"{prefix}-a", current.Protect("alpha"));
            await InsertAsync($"{prefix}-b", current.Protect("beta"));
            var before = await ReadValueAsync($"{prefix}-a");
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var rotation = scope.ServiceProvider.GetRequiredService<KeyRotationService>();

            await Assert.ThrowsAsync<ArgumentException>(() => rotation.RotateAsync("invalid-key"));
            Assert.Equal(before, await ReadValueAsync($"{prefix}-a"));

            var newKey = KeyRotationService.CreateKey();
            await rotation.RotateAsync(newKey);
            using var replacement = new AesGcmSecretProtector(newKey);
            var rotatedA = await ReadValueAsync($"{prefix}-a");
            var rotatedB = await ReadValueAsync($"{prefix}-b");
            Assert.Equal("alpha", replacement.Unprotect(rotatedA!));
            Assert.Equal("beta", replacement.Unprotect(rotatedB!));
            Assert.Throws<CryptographicException>(() => current.Unprotect(rotatedA!));
        }
        finally
        {
            await DeleteAsync(prefix);
        }
    }

    [Fact]
    public void Generated_rotation_key_is_canonical_Base64_for_exactly_32_bytes()
    {
        var encoded = KeyRotationService.CreateKey();
        AesGcmSecretProtector.ValidateKey(encoded);
        Assert.Equal(32, Convert.FromBase64String(encoded).Length);
    }

    [Fact]
    public void Checked_in_OpenAPI_contract_contains_every_public_API_group_and_no_secrets()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        FileInfo? contract = null;
        while (directory is not null && contract is null)
        {
            var candidate = new FileInfo(Path.Combine(directory.FullName, "contracts", "mtplayer-api-v1.json"));
            contract = candidate.Exists ? candidate : null;
            directory = directory.Parent;
        }

        Assert.NotNull(contract);
        var json = File.ReadAllText(contract.FullName);
        using var document = JsonDocument.Parse(json);
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/auth/login", out _));
        Assert.True(paths.TryGetProperty("/api/v1/devices", out _));
        Assert.True(paths.TryGetProperty("/api/v1/sync/push", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/settings", out _));
        Assert.DoesNotContain("DATA_ENCRYPTION_KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ADMIN_SETUP_TOKEN", json, StringComparison.Ordinal);
    }

    private async Task InsertAsync(string key, string value)
    {
        await using var db = fixture.CreateDbContext();
        db.SystemSettings.Add(new SystemSettingEntity
        {
            Key = key,
            Value = value,
            IsEncrypted = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string?> ReadValueAsync(string key)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SystemSettings.AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.Value)
            .SingleAsync();
    }

    private async Task DeleteAsync(string prefix)
    {
        await using var db = fixture.CreateDbContext();
        await db.SystemSettings.Where(setting => setting.Key.StartsWith(prefix))
            .ExecuteDeleteAsync();
    }
}
