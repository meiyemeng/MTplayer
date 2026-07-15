using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using MTPlayer.Mac.Services;

namespace MTPlayer.Mac;

public sealed partial class DetailWindow : Window, IDisposable
{
    private readonly MediaDetail _detail;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _images = new() { Timeout = TimeSpan.FromSeconds(12) };
    private int _episodeIndex;

    public DetailWindow(MediaDetail detail, AppSettings settings, SettingsStore store)
    {
        InitializeComponent();
        _detail = detail;
        _settings = settings;
        _store = store;
        TitleText.Text = detail.Item.Name;
        TypeText.Text = detail.Item.Type;
        MetaText.Text = string.Join(" · ", new[] { detail.Item.Year, detail.Item.Remarks, detail.Item.SiteName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        PeopleText.Text = $"导演：{Value(detail.Director)}\n主演：{Value(detail.Actor)}";
        ContentText.Text = Value(detail.Content, "暂无影片介绍");
        InterfaceBox.ItemsSource = new[] { detail.Item.SiteName };
        InterfaceBox.SelectedIndex = 0;
        LineBox.ItemsSource = detail.Lines.Select(x => x.Name).ToList();
        LineBox.SelectedIndex = detail.Lines.Count > 0 ? 0 : -1;
        LineBox.SelectionChanged += (_, _) => RenderEpisodes();
        PlayButton.Click += (_, _) => Play();
        FavoriteButton.Click += (_, _) => ToggleFavorite();
        UpdateFavoriteText();
        RenderEpisodes();
        Opened += async (_, _) => await LoadPosterAsync();
        Closed += (_, _) => Dispose();
    }

    private void RenderEpisodes()
    {
        EpisodesPanel.Children.Clear();
        if (LineBox.SelectedIndex < 0 || LineBox.SelectedIndex >= _detail.Lines.Count) return;
        var episodes = _detail.Lines[LineBox.SelectedIndex].Episodes;
        _episodeIndex = Math.Clamp(_episodeIndex, 0, Math.Max(0, episodes.Count - 1));
        for (var i = 0; i < episodes.Count; i++)
        {
            var index = i;
            var button = new Button { Content = episodes[i].Name, Margin = new Avalonia.Thickness(5), Width = 118, Height = 44 };
            if (i == _episodeIndex) button.Classes.Add("primary");
            button.Click += (_, _) => { _episodeIndex = index; RenderEpisodes(); };
            button.DoubleTapped += (_, _) => { _episodeIndex = index; Play(); };
            EpisodesPanel.Children.Add(button);
        }
    }

    private void Play()
    {
        if (LineBox.SelectedIndex < 0 || _detail.Lines.Count == 0) return;
        AddHistory();
        new PlayerWindow(_detail, LineBox.SelectedIndex, _episodeIndex, _settings, _store).Show();
    }

    private void ToggleFavorite()
    {
        var existing = _settings.Favorites.FirstOrDefault(x => x.SiteKey == _detail.Item.SiteKey && x.Id == _detail.Item.Id);
        if (existing is null) _settings.Favorites.Insert(0, _detail.Item);
        else _settings.Favorites.Remove(existing);
        _store.Save(_settings);
        UpdateFavoriteText();
    }

    private void AddHistory()
    {
        _settings.History.RemoveAll(x => x.SiteKey == _detail.Item.SiteKey && x.Id == _detail.Item.Id);
        _settings.History.Insert(0, _detail.Item);
        if (_settings.History.Count > 100) _settings.History.RemoveRange(100, _settings.History.Count - 100);
        _store.Save(_settings);
    }

    private void UpdateFavoriteText() => FavoriteButton.Content = _settings.Favorites.Any(x => x.SiteKey == _detail.Item.SiteKey && x.Id == _detail.Item.Id) ? "♥ 已收藏" : "♡ 加入收藏";

    private async Task LoadPosterAsync()
    {
        if (!Uri.TryCreate(_detail.Item.Poster, UriKind.Absolute, out var uri)) return;
        try { await using var stream = await _images.GetStreamAsync(uri); Poster.Source = new Bitmap(stream); } catch { }
    }

    private static string Value(string value, string fallback = "未知") => string.IsNullOrWhiteSpace(value) ? fallback : value;
    public void Dispose() => _images.Dispose();
}
