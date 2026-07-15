using System.Net;
using System.Net.Http;
using System.Windows;
using MTPlayer.Client.Core.Account;
using MTPlayer.Client.Core.Library;
using MTPlayer.Client.Core.Settings;
using MTPlayer.Client.Core.Sync;
using WebHtv.Desktop.Security;

namespace WebHtv.Desktop;

public partial class AccountWindow : Window, IDisposable
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly DpapiTokenStore _tokenStore = new(ApplicationPaths.AccountTokenFilePath);
    private readonly JsonSettingsStore _settingsStore = new(ApplicationPaths.SettingsFilePath);
    private readonly JsonLibraryStore _libraryStore = new(ApplicationPaths.LibraryFilePath);
    private readonly SyncQueueStore _queueStore = new(ApplicationPaths.SyncQueueFilePath);
    private readonly AccountApiClient _account;
    private ClientSettings _settings = new();
    private bool _busy;
    private bool _disposed;

    public AccountWindow()
    {
        InitializeComponent();
        _account = new AccountApiClient(_httpClient, _tokenStore);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsStore.LoadAsync();
        if (_settings.DeviceId == Guid.Empty)
        {
            _settings.DeviceId = Guid.NewGuid();
            await _settingsStore.SaveAsync(_settings);
        }

        ServerUrlText.Text = _settings.ServerUrl;
        if (!string.IsNullOrWhiteSpace(_settings.ServerUrl))
        {
            await BindAsync(showSuccess: false);
        }

        RefreshState();
    }

    private async void BindServer_Click(object sender, RoutedEventArgs e) => await BindAsync(showSuccess: true);

    private async Task<bool> BindAsync(bool showSuccess)
    {
        if (!TryGetBinding(out var binding))
        {
            SetStatus("请输入不带路径和端口的 HTTPS 地址，例如 https://api.example.com。", error: true);
            return false;
        }

        return await RunAsync(async () =>
        {
            await _account.BindAsync(binding!);
            _settings.ServerUrl = binding!.ToString();
            await _settingsStore.SaveAsync(_settings);
            if (showSuccess) SetStatus("服务器地址已保存。已有登录凭据时可直接同步。", error: false);
            RefreshState();
        });
    }

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureBindingAsync()) return;
        if (!ValidateCredentials(out var email, out var password)) return;
        await RunAsync(async () =>
        {
            await _account.RegisterAsync(email, password);
            SetStatus("账号已创建。若后台要求邮箱验证，请先打开验证邮件，然后点击“登录并同步”。", false);
        });
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureBindingAsync()) return;
        if (!ValidateCredentials(out var email, out var password)) return;
        await RunAsync(async () =>
        {
            await _account.LoginAsync(email, password, Environment.MachineName, "windows");
            var sync = CreateSyncEngine();
            await sync.MergeGuestDataAsync();
            var result = await sync.SynchronizeAsync(_settings.DeviceId);
            PasswordText.Clear();
            SetStatus(DescribeSync(result, "登录成功。"), result.Status != SyncRunStatus.Success);
            RefreshState();
        });
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var result = await CreateSyncEngine().SynchronizeAsync(_settings.DeviceId);
            SetStatus(DescribeSync(result, "同步完成。"), result.Status != SyncRunStatus.Success);
            RefreshState();
        });
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            await _account.LogoutAsync();
            SetStatus("已退出账号。本地收藏、记录和设置均已保留。", false);
            RefreshState();
        });
    }

    private SyncEngine CreateSyncEngine() => new(
        new AccountSyncApiClient(_account),
        _queueStore,
        _libraryStore,
        _settingsStore);

    private async Task<bool> EnsureBindingAsync()
    {
        if (_account.Binding is not null) return true;
        return await BindAsync(showSuccess: false);
    }

    private bool TryGetBinding(out ServerBinding? binding)
    {
#if DEBUG
        const bool allowInsecureLoopback = true;
#else
        const bool allowInsecureLoopback = false;
#endif
        return ServerBinding.TryCreate(ServerUrlText.Text, allowInsecureLoopback, out binding);
    }

    private bool ValidateCredentials(out string email, out string password)
    {
        email = EmailText.Text.Trim();
        password = PasswordText.Password;
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || password.Length < 10)
        {
            SetStatus("请输入有效邮箱，密码至少 10 个字符。", true);
            return false;
        }

        return true;
    }

    private async Task<bool> RunAsync(Func<Task> action)
    {
        if (_busy) return false;
        _busy = true;
        SetButtonsEnabled(false);
        try
        {
            await action();
            return true;
        }
        catch (AccountApiException exception)
        {
            SetStatus(DescribeAccountError(exception), true);
            RefreshState();
            return false;
        }
        catch (HttpRequestException)
        {
            SetStatus("无法连接同步服务器。本地播放不受影响，请检查域名与 Cloudflare Tunnel。", true);
            return false;
        }
        catch (TaskCanceledException)
        {
            SetStatus("连接同步服务器超时。本地播放不受影响。", true);
            return false;
        }
        catch (Exception exception)
        {
            SetStatus($"操作失败：{exception.Message}", true);
            return false;
        }
        finally
        {
            _busy = false;
            RefreshState();
        }
    }

    private void RefreshState()
    {
        var bound = _account.Binding is not null;
        var authenticated = _account.IsAuthenticated;
        BindingStateText.Text = bound ? "已绑定" : "未绑定";
        BindingStateText.Foreground = new System.Windows.Media.SolidColorBrush(
            bound ? System.Windows.Media.Color.FromRgb(70, 213, 154) : System.Windows.Media.Color.FromRgb(245, 185, 95));
        AccountStateText.Text = authenticated
            ? _account.EmailVerified ? "已登录 · 邮箱已验证" : "已登录 · 邮箱待验证"
            : "当前为游客模式";
        SyncBadgeText.Text = authenticated ? "可同步" : "本地可用";
        SetButtonsEnabled(!_busy);
    }

    private void SetButtonsEnabled(bool enabled)
    {
        LoginButton.IsEnabled = enabled;
        RegisterButton.IsEnabled = enabled;
        SyncButton.IsEnabled = enabled && _account.IsAuthenticated;
        LogoutButton.IsEnabled = enabled && _account.IsAuthenticated;
    }

    private void SetStatus(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(error
            ? System.Windows.Media.Color.FromRgb(255, 112, 124)
            : System.Windows.Media.Color.FromRgb(174, 181, 192));
    }

    private static string DescribeSync(SyncRunResult result, string successPrefix) => result.Status switch
    {
        SyncRunStatus.Success => $"{successPrefix} 上传 {result.Pushed} 项，下载 {result.Pulled} 项，待同步 {result.Pending} 项。",
        SyncRunStatus.Offline => "服务器暂时不可用，变更已保存在本机，稍后可再次同步。",
        _ => "登录已失效，请重新登录。",
    };

    private static string DescribeAccountError(AccountApiException exception) => exception.Code switch
    {
        "invalid_credentials" => "邮箱或密码错误。",
        "email_not_verified" => "邮箱尚未验证，请先打开验证邮件。",
        "registration_disabled" => "后台当前未开放注册。",
        "email_already_registered" => "该邮箱已经注册，请直接登录。",
        "authentication_required" or "invalid_refresh_token" => "登录已失效，请重新登录。",
        _ when exception.StatusCode == HttpStatusCode.TooManyRequests => "操作过于频繁，请稍后再试。",
        _ => $"服务器拒绝了请求：{exception.Code}",
    };

    private void Window_Closed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _account.Dispose();
        _httpClient.Dispose();
        _tokenStore.Dispose();
        _settingsStore.Dispose();
        _libraryStore.Dispose();
        _queueStore.Dispose();
        GC.SuppressFinalize(this);
    }
}
