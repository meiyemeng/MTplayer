using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace MTPlayer.Mac.Services;

public sealed class AccountService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private string _origin = string.Empty;
    public bool SignedIn { get; private set; }

    public void Bind(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.AbsolutePath.Trim('/')))
            throw new ArgumentException("服务器地址必须是 HTTPS 域名，不要填写 API 路径。");
        _origin = value.Trim().TrimEnd('/');
    }

    public async Task RegisterAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(_origin + "/api/v1/auth/register", new { email, password }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(_origin + "/api/v1/auth/login", new { email, password, deviceName = Environment.MachineName, platform = "macos" }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var refresh = document.RootElement.GetProperty("refreshToken").GetString() ?? throw new InvalidDataException("服务器未返回登录令牌");
        await SaveToKeychainAsync(refresh);
        SignedIn = true;
    }

    public async Task LogoutAsync()
    {
        if (OperatingSystem.IsMacOS())
            await RunSecurityAsync(["delete-generic-password", "-s", "cn.mtplayer.mac.refresh"]);
        SignedIn = false;
    }

    private static async Task SaveToKeychainAsync(string token)
    {
        if (!OperatingSystem.IsMacOS()) return;
        await RunSecurityAsync(["add-generic-password", "-U", "-s", "cn.mtplayer.mac.refresh", "-a", Environment.UserName, "-w", token]);
    }

    private static async Task RunSecurityAsync(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo("/usr/bin/security") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var value in arguments) info.ArgumentList.Add(value);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法访问 macOS 钥匙串");
        await process.WaitForExitAsync();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        try { using var problem = JsonDocument.Parse(raw); throw new InvalidOperationException(problem.RootElement.TryGetProperty("title", out var title) ? title.GetString() : $"HTTP {(int)response.StatusCode}"); }
        catch (JsonException) { throw new InvalidOperationException($"服务器返回 HTTP {(int)response.StatusCode}"); }
    }

    public void Dispose() => _http.Dispose();
}
