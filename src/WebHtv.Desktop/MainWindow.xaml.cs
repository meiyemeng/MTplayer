using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using WebHtv.Core.Catalogue;
using WebHtv.Playback;

namespace WebHtv.Desktop;

public partial class MainWindow : Window, IDisposable
{
    private readonly ShellViewModel _viewModel = ShellViewModel.CreateDefault();
    private readonly DispatcherTimer _configurationRefreshTimer = new() { Interval = TimeSpan.FromMinutes(20) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private string _activePage = "home";
    private bool _configurationRefreshRunning;
    private NativePlaybackService? _livePlayback;
    private ICollectionView? _liveChannelView;
    private LiveChannelGroup? _selectedLiveGroup;
    private LiveChannel? _activeLiveChannel;
    private readonly HashSet<string> _failedLiveUrls = new(StringComparer.OrdinalIgnoreCase);
    private bool _changingLiveSource;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _configurationRefreshTimer.Tick += ConfigurationRefreshTimer_Tick;
        _clockTimer.Tick += (_, _) => UpdateClock();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateClock();
        _clockTimer.Start();
        await _viewModel.LoadAsync();
        LoadSettingsControls();
        ShowPage("home");
        await _viewModel.LoadTopListsAsync();
        _configurationRefreshTimer.Start();
        await RefreshConfigurationAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _configurationRefreshTimer.Stop();
        _clockTimer.Stop();
        DisposeLivePlayback();
        base.OnClosed(e);
    }

    private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private async void ConfigurationRefreshTimer_Tick(object? sender, EventArgs e) => await RefreshConfigurationAsync();

    private async Task RefreshConfigurationAsync()
    {
        if (_configurationRefreshRunning) return;
        _configurationRefreshRunning = true;
        try
        {
            await _viewModel.ImportFromAddressAsync();
            if (_viewModel.LastConfigurationImportSucceeded)
            {
                await _viewModel.LoadTopListsAsync();
            }
        }
        finally
        {
            _configurationRefreshRunning = false;
        }
    }

    private async void ImportNetworkConfiguration_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ImportFromAddressAsync();
        LoadSettingsControls();
        if (_viewModel.LastConfigurationImportSucceeded)
        {
            await _viewModel.LoadTopListsAsync();
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("home");
        await _viewModel.SearchAsync();
    }

    private async void Poster_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PosterCard card }) return;
        var context = await _viewModel.LoadDetailAsync(card);
        if (context is null) return;
        if (_activePage == "history")
        {
            var history = await _viewModel.GetHistoryAsync(card);
            if (history is not null)
            {
                PlayerWindowLauncher.TryShow(
                    this,
                    () => new PlayerWindow(_viewModel, context, Math.Clamp(history.SourceIndex, 0, context.Detail.Sources.Count - 1), history.EpisodeIndex, history.PositionMs));
                return;
            }
        }
        new MovieDetailWindow(_viewModel, context) { Owner = this }.Show();
    }

    private async void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string page }) return;
        if (page == "search") { ShowPage("home"); SearchBox.Focus(); return; }
        if (page == "account")
        {
            new AccountWindow { Owner = this }.ShowDialog();
            return;
        }
        if (page == "home") _viewModel.ShowHomeTopLists();
        ShowPage(page);
        if (page == "favorites") { LibraryTitle.Text = "我的收藏"; LibrarySubtitle.Text = "收藏的影片保存在本机。"; await _viewModel.LoadFavoritesAsync(); }
        else if (page == "history") { LibraryTitle.Text = "观看记录"; LibrarySubtitle.Text = "点击影片从上次位置继续播放。"; await _viewModel.LoadHistoryAsync(); }
        else if (page == "live")
        {
            await _viewModel.LoadLiveChannelsAsync();
            ConfigureLiveChannelView();
        }
        else if (page == "settings") LoadSettingsControls();
    }

    private void ShowPage(string page)
    {
        if (_activePage == "live" && page != "live") StopLivePlayback();
        _activePage = page;
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = page is "favorites" or "history" ? Visibility.Visible : Visibility.Collapsed;
        LivePage.Visibility = page == "live" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in FindVisualChildren<Button>(this).Where(button => button.Tag is string))
        {
            var selected = Equals(button.Tag, page);
            button.Background = selected
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(102, 0, 0, 0))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(51, 0, 0, 0));
            button.BorderBrush = selected ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Transparent;
            button.Foreground = System.Windows.Media.Brushes.White;
        }
    }

    private void PosterSettings_Click(object sender, RoutedEventArgs e) => _viewModel.TogglePosterSettings();
    private async void CompactPosterWall_Click(object sender, RoutedEventArgs e) => await _viewModel.SetPosterWidthAsync(132, "紧凑");
    private async void StandardPosterWall_Click(object sender, RoutedEventArgs e) => await _viewModel.SetPosterWidthAsync(156, "标准");
    private async void ComfortablePosterWall_Click(object sender, RoutedEventArgs e) => await _viewModel.SetPosterWidthAsync(180, "舒展");

    private void ConfigureLiveChannelView()
    {
        _liveChannelView = CollectionViewSource.GetDefaultView(_viewModel.LiveChannelGroups);
        _liveChannelView.Filter = item => item is LiveChannelGroup group &&
            LiveChannelOrganizer.Matches(group, LiveSearchBox.Text, LiveCategoryList.SelectedItem as string);
        if (LiveCategoryList.SelectedIndex < 0 && LiveCategoryList.Items.Count > 0)
        {
            LiveCategoryList.SelectedIndex = 0;
        }
        RefreshLiveChannelView();
    }

    private void LiveSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshLiveChannelView();

    private void LiveCategory_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshLiveChannelView();

    private void RefreshLiveChannelView()
    {
        if (_liveChannelView is null) return;
        _liveChannelView.Refresh();
        LiveChannelCountText.Text = $"{_liveChannelView.Cast<object>().Count()} 个频道";
    }

    private async void LiveChannelGroup_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LiveChannelList.SelectedItem is not LiveChannelGroup group || group.Sources.Count == 0) return;
        _selectedLiveGroup = group;
        _failedLiveUrls.Clear();
        _changingLiveSource = true;
        LiveSourceSelector.ItemsSource = group.Sources;
        LiveSourceSelector.SelectedIndex = 0;
        _changingLiveSource = false;
        LiveChannelTitleText.Text = group.Name;
        LiveNowPlayingText.Text = group.ProgrammeText;
        await PlayLiveSourceAsync(group.Sources[0]);
    }

    private async void LiveSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingLiveSource || LiveSourceSelector.SelectedItem is not LiveChannelSourceOption source) return;
        _failedLiveUrls.Clear();
        await PlayLiveSourceAsync(source);
    }

    private bool EnsureLivePlayback()
    {
        if (_livePlayback is not null) return true;
        try
        {
            _livePlayback = new NativePlaybackService(_viewModel.Settings.HardwareDecode);
            _livePlayback.Player.Playing += LivePlayer_Playing;
            _livePlayback.Player.EncounteredError += LivePlayer_EncounteredError;
            LiveVideoOutput.MediaPlayer = _livePlayback.Player;
            return true;
        }
        catch (Exception exception)
        {
            ShowLiveStatus($"播放器启动失败：{exception.Message}");
            return false;
        }
    }

    private async Task PlayLiveSourceAsync(LiveChannelSourceOption source)
    {
        if (!EnsureLivePlayback()) return;
        _activeLiveChannel = source.Channel;
        ShowLiveStatus($"正在连接 {source.Label}…");
        try
        {
            _livePlayback!.Player.Stop();
            await _livePlayback.OpenAsync(new PlayRequest(
                source.Channel.Url,
                source.Channel.Group,
                false,
                source.Channel.Headers));
        }
        catch (Exception exception)
        {
            ShowLiveStatus($"播放失败：{exception.Message}");
        }
    }

    private void LivePlayer_Playing(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_livePlayback is null) return;
        _livePlayback.Player.Volume = _viewModel.Settings.DefaultVolume;
        LiveStatusOverlay.Visibility = Visibility.Collapsed;
    });

    private void LivePlayer_EncounteredError(object? sender, EventArgs e)
    {
        // The media engine raises this from its own worker thread, so marshal the UI
        // work back onto the dispatcher. Avoid an async lambda inside BeginInvoke: an
        // exception there would otherwise become an unobserved fault. Playback itself
        // is started fire-and-forget; PlayLiveSourceAsync handles its own failures.
        Dispatcher.BeginInvoke(() =>
        {
            if (_activePage != "live" || _activeLiveChannel is null || _selectedLiveGroup is null) return;
            _failedLiveUrls.Add(_activeLiveChannel.Url);
            var next = _selectedLiveGroup.Sources.FirstOrDefault(source => !_failedLiveUrls.Contains(source.Channel.Url));
            if (next is null)
            {
                ShowLiveStatus("该频道的所有播放源均连接失败，请稍后重试。");
                return;
            }

            ShowLiveStatus($"当前源不可用，正在切换到 {next.Label}…");
            _changingLiveSource = true;
            LiveSourceSelector.SelectedItem = next;
            _changingLiveSource = false;
            _ = PlayLiveSourceAsync(next);
        });
    }

    private void ShowLiveStatus(string message)
    {
        LiveStatusText.Text = message;
        LiveStatusOverlay.Visibility = Visibility.Visible;
    }

    private void StopLivePlayback()
    {
        _activeLiveChannel = null;
        _selectedLiveGroup = null;
        _failedLiveUrls.Clear();
        _livePlayback?.Player.Stop();
        ShowLiveStatus("选择左侧频道开始播放");
    }

    private void DisposeLivePlayback()
    {
        if (_livePlayback is null) return;
        _livePlayback.Player.Playing -= LivePlayer_Playing;
        _livePlayback.Player.EncounteredError -= LivePlayer_EncounteredError;
        LiveVideoOutput.MediaPlayer = null;
        _livePlayback.Dispose();
        _livePlayback = null;
    }

    public void Dispose()
    {
        DisposeLivePlayback();
        GC.SuppressFinalize(this);
    }

    private void LoadSettingsControls()
    {
        var settings = _viewModel.Settings;
        HardwareDecodeCheck.IsChecked = settings.HardwareDecode;
        DefaultSpeedCombo.ItemsSource = new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };
        DefaultSpeedCombo.SelectedItem = settings.DefaultSpeed;
        DefaultVolumeSlider.Value = settings.DefaultVolume;
        AutoFullscreenCheck.IsChecked = settings.AutoFullscreen;
        UseSourceCoversCheck.IsChecked = settings.UseSourceCovers;
        TmdbKeyText.Text = settings.TmdbApiKey;
        SpiderGatewayUrlText.Text = settings.SpiderGatewayUrl;
        SpiderGatewayTokenText.Text = settings.SpiderGatewayToken;
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = _viewModel.Settings;
        settings.HardwareDecode = HardwareDecodeCheck.IsChecked == true;
        settings.DefaultSpeed = DefaultSpeedCombo.SelectedItem is double speed ? speed : 1.0;
        settings.DefaultVolume = (int)DefaultVolumeSlider.Value;
        settings.AutoFullscreen = AutoFullscreenCheck.IsChecked == true;
        settings.UseSourceCovers = UseSourceCoversCheck.IsChecked == true;
        settings.TmdbApiKey = TmdbKeyText.Text.Trim();
        settings.SpiderGatewayUrl = SpiderGatewayUrlText.Text.Trim();
        settings.SpiderGatewayToken = SpiderGatewayTokenText.Text.Trim();
        await _viewModel.SaveSettingsAsync(settings);
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e) => await _viewModel.ClearHistoryAsync();

    private async void AddConfigurationSource_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.AddConfigurationSourceAsync(ConfigurationSourceNameText.Text, ConfigurationSourceAddressText.Text);
        ConfigurationSourceNameText.Clear();
        LoadSettingsControls();
        if (_viewModel.LastConfigurationImportSucceeded)
        {
            await _viewModel.LoadTopListsAsync();
        }
    }

    private async void RefreshConfigurationSource_Click(object sender, RoutedEventArgs e)
    {
        if (_configurationRefreshRunning) return;
        _configurationRefreshRunning = true;
        try
        {
            await _viewModel.UpdateActiveConfigurationSourceAsync(ConfigurationSourceAddressText.Text);
            if (_viewModel.LastConfigurationImportSucceeded)
            {
                await _viewModel.LoadTopListsAsync();
            }
            else
            {
                MessageBox.Show(this, _viewModel.StatusMessage, "配置更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _configurationRefreshRunning = false;
        }
    }

    private async void ActivateConfigurationSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ConfigurationSourceEntry entry })
        {
            await _viewModel.ActivateConfigurationSourceAsync(entry);
            if (_viewModel.LastConfigurationImportSucceeded)
            {
                await _viewModel.LoadTopListsAsync();
            }
        }
    }

    private async void RemoveConfigurationSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ConfigurationSourceEntry entry }) await _viewModel.RemoveConfigurationSourceAsync(entry);
    }

    private async void AddLiveSource_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.AddCustomLiveSourceAsync(LiveSourceNameText.Text, LiveSourceAddressText.Text, LiveEpgAddressText.Text);
        LiveSourceNameText.Clear();
        LiveSourceAddressText.Clear();
        LiveEpgAddressText.Clear();
    }

    private async void RemoveLiveSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CustomLiveSourceEntry entry }) await _viewModel.RemoveCustomLiveSourceAsync(entry);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject source) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(source); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(source, index);
            if (child is T result) yield return result;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
}
