using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MTPlayer.Mac.Services;

namespace MTPlayer.Mac;

public sealed partial class MainWindow : Window, IDisposable
{
    private static readonly IBrush PageBackground = Brush.Parse("#090C10");
    private static readonly IBrush Surface = Brush.Parse("#151920");
    private static readonly IBrush Muted = Brush.Parse("#9BA3AE");
    private static readonly IBrush Red = Brush.Parse("#F0192D");
    private readonly SettingsStore _store = new();
    private readonly CatalogueService _catalogue = new();
    private readonly AccountService _account = new();
    private readonly HttpClient _images = new() { Timeout = TimeSpan.FromSeconds(12) };
    private AppSettings _settings;
    private List<Site> _sites = [];

    public MainWindow()
    {
        InitializeComponent();
        _settings = _store.Load();
        Opened += async (_, _) => await ShowHomeAsync();
        Closed += (_, _) => { _catalogue.Dispose(); _account.Dispose(); _images.Dispose(); };
    }

    private async Task ShowHomeAsync()
    {
        var (scroll, root) = Page("MT 精选", "热门片单");
        var status = Note("正在读取配置源…");
        root.Children.Add(status);
        PageHost.Content = scroll;
        try
        {
            _sites = await _catalogue.LoadSitesAsync(_settings.ConfigurationGroups);
            if (_sites.Count == 0) { status.Text = "还没有可用配置源，请在设置中添加 HTTPS 配置地址。"; return; }
            var items = await _catalogue.LatestAsync(_sites, 60);
            status.Text = $"已启用 {_sites.Count} 个可用接口";
            foreach (var (title, filter) in new[] { ("电影 Top 10", "电影"), ("电视剧 Top 10", "电视|剧"), ("动漫电影 Top 10", "动漫|动画"), ("动漫番剧 Top 10", "番剧|动漫"), ("综艺 Top 10", "综艺") })
            {
                var selected = items.Where(x => (x.Type + x.Name).ContainsAny(filter.Split('|'))).Take(10).ToList();
                if (selected.Count == 0) selected = items.Take(10).ToList();
                root.Children.Add(Heading(title, 24));
                root.Children.Add(PosterRow(selected));
            }
        }
        catch (Exception ex) { status.Text = "读取失败：" + ex.Message; }
    }

    private void ShowSearch()
    {
        var root = ContentPage("全接口聚合", "搜索影片");
        var input = new TextBox { Watermark = "输入片名", FontSize = 17 };
        var button = Primary("搜索");
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110"), ColumnSpacing = 12 };
        bar.Children.Add(input); Grid.SetColumn(button, 1); bar.Children.Add(button);
        var status = Note("会并发查询所有已启用接口，只显示实际返回结果。");
        var results = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 180, ItemHeight = 320 };
        root.Children.Add(bar); root.Children.Add(status); root.Children.Add(results);
        PageHost.Content = new ScrollViewer { Content = root };
        button.Click += async (_, _) =>
        {
            var keyword = input.Text?.Trim(); if (string.IsNullOrEmpty(keyword)) return;
            button.IsEnabled = false; status.Text = "正在搜索所有接口…"; results.Children.Clear();
            try
            {
                if (_sites.Count == 0) _sites = await _catalogue.LoadSitesAsync(_settings.ConfigurationGroups);
                var items = await _catalogue.SearchAsync(_sites, keyword);
                status.Text = items.Count == 0 ? "没有找到影片，请检查配置源或更换关键词。" : $"找到 {items.Count} 个接口结果";
                foreach (var item in items) results.Children.Add(PosterCard(item));
            }
            catch (Exception ex) { status.Text = "搜索失败：" + ex.Message; }
            finally { button.IsEnabled = true; }
        };
    }

    private void ShowLibrary(bool favorites)
    {
        var root = ContentPage("本地保存", favorites ? "我的收藏" : "观看记录");
        var values = favorites ? _settings.Favorites : _settings.History;
        root.Children.Add(Note(values.Count == 0 ? "这里暂时没有内容；退出登录不会清除本地数据。" : $"共 {values.Count} 条本地记录"));
        var grid = new WrapPanel { ItemWidth = 180, ItemHeight = 320 };
        foreach (var item in values) grid.Children.Add(PosterCard(item));
        root.Children.Add(grid);
        PageHost.Content = new ScrollViewer { Content = root };
    }

    private async void ShowLive()
    {
        var root = ContentPage("直播频道", "打开 M3U8 / 直播流");
        root.Children.Add(Note("输入你拥有播放权限的 HTTP 或 HTTPS 直播流地址。"));
        var url = new TextBox { Watermark = "https://.../live.m3u8" };
        var play = Primary("开始播放");
        play.HorizontalAlignment = HorizontalAlignment.Left;
        play.Click += (_, _) =>
        {
            if (!Uri.TryCreate(url.Text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
            new PlayerWindow(uri.ToString(), "直播频道", "live:" + uri.GetHashCode()).Show();
        };
        var status = Note("正在读取配置中的直播频道…");
        var channels = new WrapPanel { ItemWidth = 280, ItemHeight = 58 };
        root.Children.Add(url); root.Children.Add(play); root.Children.Add(status); root.Children.Add(channels); PageHost.Content = new ScrollViewer { Content = root };
        var values = await _catalogue.LoadLiveChannelsAsync(_settings.ConfigurationGroups);
        status.Text = values.Count == 0 ? "配置中没有读取到可用直播频道。" : $"共识别 {values.Count} 个直播频道，点击即可播放。";
        foreach (var channel in values)
        {
            var button = Secondary($"{channel.Group} · {channel.Name}");
            button.Click += (_, _) => new PlayerWindow(channel.Url, channel.Name, "live:" + channel.Url.GetHashCode()).Show();
            channels.Children.Add(button);
        }
    }

    private void ShowAccount()
    {
        var root = ContentPage("跨设备同步", "账户与服务器");
        var server = new TextBox { Watermark = "https://你的同步域名", Text = _settings.ServerUrl };
        var email = new TextBox { Watermark = "邮箱" };
        var password = new TextBox { Watermark = "密码（至少 10 位）", PasswordChar = '●' };
        var status = Note(_account.SignedIn ? "已登录，可以同步。" : "游客模式：本地播放可用，但不会同步。");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var login = Primary("登录"); var register = Secondary("注册"); var logout = Secondary("退出登录");
        buttons.Children.Add(login); buttons.Children.Add(register); buttons.Children.Add(logout);
        root.Children.Add(server); root.Children.Add(email); root.Children.Add(password); root.Children.Add(status); root.Children.Add(buttons);
        PageHost.Content = root;
        async Task Run(bool isRegister)
        {
            try
            {
                _account.Bind(server.Text ?? string.Empty); _settings.ServerUrl = (server.Text ?? string.Empty).Trim(); _store.Save(_settings);
                status.Text = isRegister ? "正在注册…" : "正在登录…";
                if (isRegister) { await _account.RegisterAsync(email.Text ?? "", password.Text ?? ""); status.Text = "注册请求已提交，请按服务器设置完成邮箱验证。"; }
                else { await _account.LoginAsync(email.Text ?? "", password.Text ?? ""); status.Text = "登录成功，可以同步。"; }
            }
            catch (Exception ex) { status.Text = (isRegister ? "注册失败：" : "登录失败：") + ex.Message; }
        }
        login.Click += async (_, _) => await Run(false); register.Click += async (_, _) => await Run(true);
        logout.Click += async (_, _) => { await _account.LogoutAsync(); status.Text = "已退出登录，本地收藏、记录和配置没有删除。"; };
    }

    private void ShowSettings()
    {
        var root = ContentPage("数据来源", "配置源管理");
        root.Children.Add(Note("可添加多个 TVBox 单仓或接口组，启用的配置会共同参与搜索。"));
        var name = new TextBox { Watermark = "配置源名称" };
        var url = new TextBox { Watermark = "https://... 配置接口" };
        var add = Primary("导入并启用");
        root.Children.Add(name); root.Children.Add(url); root.Children.Add(add);
        var list = new StackPanel { Spacing = 10, Margin = new Thickness(0, 18, 0, 0) };
        root.Children.Add(list);
        void Render()
        {
            list.Children.Clear();
            foreach (var group in _settings.ConfigurationGroups.ToList())
            {
                var toggle = new CheckBox { Content = group.Name, IsChecked = group.Enabled, FontSize = 17, VerticalAlignment = VerticalAlignment.Center };
                var remove = Secondary("删除");
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,100") };
                row.Children.Add(toggle); Grid.SetColumn(remove, 1); row.Children.Add(remove);
                toggle.IsCheckedChanged += (_, _) => { group.Enabled = toggle.IsChecked == true; _store.Save(_settings); };
                remove.Click += (_, _) => { _settings.ConfigurationGroups.Remove(group); _store.Save(_settings); Render(); };
                list.Children.Add(new Border { Background = Surface, Padding = new Thickness(14), CornerRadius = new CornerRadius(10), Child = row });
                list.Children.Add(Note(group.Url));
            }
        }
        add.Click += (_, _) =>
        {
            if (!Uri.TryCreate(url.Text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
            var existing = _settings.ConfigurationGroups.FirstOrDefault(x => string.Equals(x.Url, uri.ToString(), StringComparison.OrdinalIgnoreCase));
            if (existing is null) _settings.ConfigurationGroups.Add(new SourceGroup { Name = string.IsNullOrWhiteSpace(name.Text) ? $"配置源 {_settings.ConfigurationGroups.Count + 1}" : name.Text.Trim(), Url = uri.ToString(), Enabled = true });
            else existing.Enabled = true;
            _store.Save(_settings); name.Text = string.Empty; url.Text = string.Empty; Render();
        };
        Render(); PageHost.Content = new ScrollViewer { Content = root };
    }

    private void ShowAbout()
    {
        var root = ContentPage("关于软件", "MT播放器 macOS 1.3.0");
        root.Children.Add(Note("原生 macOS 桌面客户端 · Intel 64 位（Apple Silicon 可通过 Rosetta 2 运行）\n\n源码仓库：https://github.com/meiyemeng/MTplayer\n客户端下载：https://github.com/meiyemeng/MTplayer/releases/latest\n\n软件不预置、不存储、不上传、不分发任何影视内容，仅播放用户自行配置且有权访问的媒体。"));
        root.Children.Add(new TextBlock { Text = "支持项目", Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 20, 0, 8) });
        using var donationStream = AssetLoader.Open(new Uri("avares://MTPlayer/Assets/alipay-donate.png"));
        root.Children.Add(new Image { Source = new Bitmap(donationStream), Width = 260, Height = 390, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left });
        root.Children.Add(Note("支付宝扫码为自愿捐助，不解锁内容或会员权益，也不代表购买影视服务。"));
        PageHost.Content = root;
    }

    private ScrollViewer PosterRow(List<MediaEntry> items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach (var item in items) row.Children.Add(PosterCard(item));
        return new ScrollViewer { Content = row, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Height = 320 };
    }

    private Button PosterCard(MediaEntry item)
    {
        var image = new Image { Width = 166, Height = 232, Stretch = Stretch.UniformToFill };
        var name = new TextBlock { Text = item.Name, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Width = 166 };
        var meta = new TextBlock { Text = $"{item.SiteName}  {item.Remarks}", Foreground = Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Width = 166 };
        var content = new StackPanel { Spacing = 7, Children = { image, name, meta } };
        var button = new Button { Content = content, Width = 180, Height = 308, Padding = new Thickness(7), Background = PageBackground, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Top };
        button.Click += async (_, _) => await OpenDetailAsync(item);
        _ = LoadPosterAsync(image, item.Poster);
        return button;
    }

    private async Task LoadPosterAsync(Image image, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        try { await using var stream = await _images.GetStreamAsync(uri); var bitmap = new Bitmap(stream); await Dispatcher.UIThread.InvokeAsync(() => image.Source = bitmap); } catch { }
    }

    private async Task OpenDetailAsync(MediaEntry item)
    {
        try
        {
            if (_sites.Count == 0) _sites = await _catalogue.LoadSitesAsync(_settings.ConfigurationGroups);
            var site = _sites.FirstOrDefault(x => x.Key == item.SiteKey) ?? throw new InvalidOperationException("该接口当前未启用");
            var detail = await _catalogue.DetailAsync(site, item.Id);
            var window = new DetailWindow(detail, _settings, _store);
            await window.ShowDialog(this);
        }
        catch (Exception ex) { await Message("详情加载失败", ex.Message); }
    }

    private async Task Message(string title, string message)
    {
        var close = Primary("确定"); var dialog = new Window { Title = title, Width = 480, Height = 230, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new StackPanel { Margin = new Thickness(26), Spacing = 18, Children = { Heading(title, 24), Note(message), close } } };
        close.Click += (_, _) => dialog.Close(); await dialog.ShowDialog(this);
    }

    private static (ScrollViewer, StackPanel) Page(string eyebrow, string title) { var root = ContentPage(eyebrow, title); return (new ScrollViewer { Content = root }, root); }
    private static StackPanel ContentPage(string eyebrow, string title) => new() { Margin = new Thickness(38, 30), Spacing = 14, Children = { new TextBlock { Text = eyebrow, Foreground = Red, FontSize = 14 }, Heading(title, 34) } };
    private static TextBlock Heading(string text, double size) => new() { Text = text, FontSize = size, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
    private static TextBlock Note(string text) => new() { Text = text, FontSize = 15, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private static Button Primary(string text) => new() { Content = text, Classes = { "primary" } };
    private static Button Secondary(string text) => new() { Content = text, Background = Brush.Parse("#20252D"), Foreground = Brushes.White };

    private async void Home_Click(object? sender, RoutedEventArgs e) => await ShowHomeAsync();
    private void Search_Click(object? sender, RoutedEventArgs e) => ShowSearch();
    private void Favorites_Click(object? sender, RoutedEventArgs e) => ShowLibrary(true);
    private void History_Click(object? sender, RoutedEventArgs e) => ShowLibrary(false);
    private void Live_Click(object? sender, RoutedEventArgs e) => ShowLive();
    private void Account_Click(object? sender, RoutedEventArgs e) => ShowAccount();
    private void Settings_Click(object? sender, RoutedEventArgs e) => ShowSettings();
    private void About_Click(object? sender, RoutedEventArgs e) => ShowAbout();

    public void Dispose()
    {
        _catalogue.Dispose();
        _account.Dispose();
        _images.Dispose();
    }
}

file static class StringExtensions
{
    public static bool ContainsAny(this string value, IEnumerable<string> parts) => parts.Any(value.Contains);
}
