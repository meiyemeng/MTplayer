using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WebHtv.Desktop;

public partial class MovieDetailWindow : Window
{
    private readonly ShellViewModel _viewModel;
    private readonly MovieDetailContext _context;
    private MovieDetailContext _activeContext;
    private bool _initializing = true;

    internal MovieDetailWindow(ShellViewModel viewModel, MovieDetailContext context)
    {
        _viewModel = viewModel;
        _context = context;
        _activeContext = context;
        InitializeComponent();
        DataContext = context;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "正在检测包含该影片且可连接的播放接口…";
        InterfaceSelector.IsEnabled = false;
        var availableInterfaces = await _viewModel.FindAvailablePlaybackInterfacesAsync(_context);
        InterfaceSelector.ItemsSource = availableInterfaces;
        InterfaceSelector.SelectedIndex = availableInterfaces.Count > 0 ? 0 : -1;
        if (availableInterfaces.Count > 0)
        {
            _activeContext = availableInterfaces[0].Context;
            SourceSelector.ItemsSource = _activeContext.Detail.Sources;
            SourceSelector.SelectedIndex = ShellViewModel.GetPreferredSourceIndex(_activeContext);
        }
        else
        {
            SourceSelector.ItemsSource = null;
            SourceSelector.IsEnabled = false;
            PlayButton.IsEnabled = false;
        }
        _initializing = false;
        InterfaceSelector.IsEnabled = availableInterfaces.Count > 0;
        StatusText.Text = availableInterfaces.Count > 0
            ? $"已验证 {availableInterfaces.Count} 个可播放接口；无该影片、接口失效或媒体不可达的接口已隐藏。"
            : "没有验证到可播放接口，请返回搜索其他片源。";
        MetadataText.Text = BuildMetadata();
        FavoriteButton.Content = await _viewModel.IsFavoriteAsync(_context.Card) ? "♥ 已收藏" : "♡ 加入收藏";
    }

    private string BuildMetadata()
    {
        var metadata = _context.Detail.Metadata;
        return string.Join(" · ", new[] { metadata.Year, metadata.Area, metadata.Language, metadata.Score is { Length: > 0 } ? $"评分 {metadata.Score}" : string.Empty, _context.Detail.Item.Remarks }
            .Where(value => !string.IsNullOrWhiteSpace(value))) +
            (string.IsNullOrWhiteSpace(metadata.Director) ? string.Empty : $"\n导演：{metadata.Director}") +
            (string.IsNullOrWhiteSpace(metadata.Actors) ? string.Empty : $"\n主演：{metadata.Actors}");
    }

    private void SourceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceSelector.SelectedItem is not WebHtv.Core.Catalogue.EpisodeSource source) return;
        EpisodeList.ItemsSource = source.Episodes;
        EpisodeList.DisplayMemberPath = "Name";
        EpisodeList.SelectedIndex = 0;
    }

    private void InterfaceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || InterfaceSelector.SelectedItem is not PlaybackInterfaceOption option) return;
        _activeContext = option.Context;
        SourceSelector.ItemsSource = option.Context.Detail.Sources;
        SourceSelector.SelectedIndex = ShellViewModel.GetPreferredSourceIndex(option.Context);
        StatusText.Text = $"当前接口：{option.Name}，请选择线路与剧集。";
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        FavoriteButton.Content = await _viewModel.ToggleFavoriteAsync(_context.Card) ? "♥ 已收藏" : "♡ 加入收藏";
        StatusText.Text = _viewModel.StatusMessage;
    }

    private void Play_Click(object sender, RoutedEventArgs e) => OpenPlayer();

    private void EpisodeList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenPlayer();

    private void OpenPlayer()
    {
        if (SourceSelector.SelectedIndex < 0 || EpisodeList.SelectedIndex < 0) return;
        new PlayerWindow(_viewModel, _activeContext, SourceSelector.SelectedIndex, EpisodeList.SelectedIndex, 0).Show();
    }
}

internal sealed record PlaybackInterfaceOption(string Name, string RuntimeKey, MovieDetailContext Context);
