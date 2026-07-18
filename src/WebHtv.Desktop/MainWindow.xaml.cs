using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WebHtv.Desktop;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _viewModel = ShellViewModel.CreateDefault();
    private readonly DispatcherTimer _configurationRefreshTimer = new() { Interval = TimeSpan.FromMinutes(20) };
    private string _activePage = "home";
    private bool _configurationRefreshRunning;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _configurationRefreshTimer.Tick += ConfigurationRefreshTimer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
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
        base.OnClosed(e);
    }

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
                new PlayerWindow(_viewModel, context, Math.Clamp(history.SourceIndex, 0, context.Detail.Sources.Count - 1), history.EpisodeIndex, history.PositionMs).Show();
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
        else if (page == "live") await _viewModel.LoadLiveChannelsAsync();
        else if (page == "settings") LoadSettingsControls();
    }

    private void ShowPage(string page)
    {
        _activePage = page;
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = page is "favorites" or "history" ? Visibility.Visible : Visibility.Collapsed;
        LivePage.Visibility = page == "live" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in FindVisualChildren<Button>(this).Where(button => button.Tag is string))
        {
            button.Background = Equals(button.Tag, page) ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 29, 33)) : System.Windows.Media.Brushes.Transparent;
            button.Foreground = Equals(button.Tag, page) ? System.Windows.Media.Brushes.White : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(199, 201, 205));
        }
    }

    private void PosterSettings_Click(object sender, RoutedEventArgs e) => _viewModel.TogglePosterSettings();
    private async void CompactPosterWall_Click(object sender, RoutedEventArgs e) => await _viewModel.SetPosterWidthAsync(132, "紧凑");
    private async void StandardPosterWall_Click(object sender, RoutedEventArgs e) => await _viewModel.SetPosterWidthAsync(156, "标准");
    private async void ComfortablePosterWall_Click(object sender, RoutedEventArgs e) => await _viewModel.SetPosterWidthAsync(180, "舒展");

    private void LiveChannel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LiveChannel channel }) return;
        var request = new WebHtv.Core.Catalogue.PlayRequest(channel.Url, channel.Group, false, channel.Headers);
        new PlayerWindow(channel.Name, request, _viewModel.Settings).Show();
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
