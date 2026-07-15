using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using MTPlayer.Mac.Services;
using System.Globalization;

namespace MTPlayer.Mac;

public sealed partial class PlayerWindow : Window, IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private Media? _media;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _hideClock;
    private readonly MediaDetail? _detail;
    private readonly AppSettings? _settings;
    private readonly SettingsStore? _store;
    private int _lineIndex;
    private int _episodeIndex;
    private readonly string _mediaKey;
    private bool _updating;
    private int _idleSeconds;

    public PlayerWindow(MediaDetail detail, int lineIndex, int episodeIndex, AppSettings settings, SettingsStore store)
        : this(detail.Lines[lineIndex].Episodes[episodeIndex].Url, detail.Item.Name, $"{detail.Item.SiteKey}:{detail.Item.Id}")
    {
        _detail = detail;
        _settings = settings;
        _store = store;
        _lineIndex = lineIndex;
        _episodeIndex = episodeIndex;
        UpdateEpisodeText();
        PreviousButton.IsVisible = true;
    }

    public PlayerWindow(string url, string title, string mediaKey)
    {
        InitializeComponent();
        _mediaKey = mediaKey;
        Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--network-caching=1500");
        _player = new MediaPlayer(_libVlc) { Volume = 80 };
        VideoSurface.MediaPlayer = _player;
        TitleText.Text = title;
        PreviousButton.IsVisible = false;
        SpeedBox.ItemsSource = new[] { "0.5×", "0.75×", "1.0×", "1.25×", "1.5×", "2.0×" };
        SpeedBox.SelectedIndex = 2;

        PauseButton.Click += (_, _) => TogglePause();
        BackButton.Click += (_, _) => SeekBy(-10_000);
        ForwardButton.Click += (_, _) => SeekBy(10_000);
        PreviousButton.Click += (_, _) => ChangeEpisode(-1);
        IntroButton.Click += (_, _) => SaveSkip(true);
        OutroButton.Click += (_, _) => SaveSkip(false);
        ClearSkipButton.Click += (_, _) => ClearSkip();
        MuteButton.Click += (_, _) => ToggleMute();
        FullScreenButton.Click += (_, _) => ToggleFullScreen();
        Volume.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_updating) _player.Volume = (int)Volume.Value; };
        Progress.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_updating && _player.Length > 0) _player.Time = (long)(Progress.Value * _player.Length); };
        SpeedBox.SelectionChanged += (_, _) => _player.SetRate(SpeedBox.SelectedIndex switch { 0 => .5f, 1 => .75f, 3 => 1.25f, 4 => 1.5f, 5 => 2f, _ => 1f });
        PointerMoved += (_, _) => RevealControls();
        PointerPressed += (_, _) => RevealControls();
        KeyDown += OnKeyDown;
        Opened += (_, _) => PlayUrl(url);
        Closed += (_, _) => Dispose();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        _hideClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hideClock.Tick += (_, _) => { if (++_idleSeconds >= 5) Overlay.IsVisible = false; };
        _hideClock.Start();
        UpdateSkipText();
    }

    private void PlayUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        _media?.Dispose();
        _media = new Media(_libVlc, uri);
        _player.Play(_media);
        PauseButton.Content = "暂停";
    }

    private void TogglePause()
    {
        if (_player.IsPlaying) { _player.Pause(); PauseButton.Content = "播放"; }
        else { _player.Play(); PauseButton.Content = "暂停"; }
    }

    private void SeekBy(long milliseconds) => _player.Time = Math.Clamp(_player.Time + milliseconds, 0, Math.Max(0, _player.Length - 1));

    private void ChangeEpisode(int offset)
    {
        if (_detail is null) return;
        var episodes = _detail.Lines[_lineIndex].Episodes;
        var next = _episodeIndex + offset;
        if (next < 0 || next >= episodes.Count) return;
        _episodeIndex = next;
        UpdateEpisodeText();
        PlayUrl(episodes[_episodeIndex].Url);
    }

    private void UpdateEpisodeText()
    {
        if (_detail is null) return;
        EpisodeText.Text = $"{_detail.Lines[_lineIndex].Name} · {_detail.Lines[_lineIndex].Episodes[_episodeIndex].Name}";
    }

    private void SaveSkip(bool intro)
    {
        if (_settings is null || _store is null || _player.Length <= 0) return;
        if (!_settings.SkipSettings.TryGetValue(_mediaKey, out var value)) _settings.SkipSettings[_mediaKey] = value = new SkipSetting();
        if (intro) value.IntroSeconds = Math.Round(_player.Time / 1000d);
        else value.OutroSeconds = Math.Round(Math.Max(0, _player.Length - _player.Time) / 1000d);
        _store.Save(_settings);
        UpdateSkipText();
    }

    private void ClearSkip()
    {
        if (_settings is null || _store is null) return;
        _settings.SkipSettings.Remove(_mediaKey);
        _store.Save(_settings);
        UpdateSkipText();
    }

    private void UpdateSkipText()
    {
        var value = _settings is not null && _settings.SkipSettings.TryGetValue(_mediaKey, out var setting) ? setting : null;
        SkipText.Text = value is null ? "未设置" : $"片头 {value.IntroSeconds:0}s / 片尾 {value.OutroSeconds:0}s";
    }

    private void UpdateClock()
    {
        var length = _player.Length;
        var time = _player.Time;
        _updating = true;
        Progress.Value = length > 0 ? Math.Clamp((double)time / length, 0, 1) : 0;
        Volume.Value = _player.Volume;
        CurrentText.Text = Format(time);
        DurationText.Text = Format(length);
        _updating = false;
        if (_settings is null || !_settings.SkipSettings.TryGetValue(_mediaKey, out var skip) || length <= 0) return;
        if (skip.IntroSeconds > 0 && time > 0 && time < skip.IntroSeconds * 1000) _player.Time = (long)(skip.IntroSeconds * 1000);
        if (skip.OutroSeconds > 0 && time >= length - skip.OutroSeconds * 1000) ChangeEpisode(1);
    }

    private void ToggleMute()
    {
        _player.Mute = !_player.Mute;
        MuteButton.Content = _player.Mute ? "🔇" : "🔊";
    }

    private void ToggleFullScreen()
    {
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
        FullScreenButton.Content = WindowState == WindowState.FullScreen ? "退出全屏" : "全屏";
    }

    private void RevealControls() { _idleSeconds = 0; Overlay.IsVisible = true; }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        RevealControls();
        if (e.Key == Key.Space) TogglePause();
        else if (e.Key == Key.Left) SeekBy(-10_000);
        else if (e.Key == Key.Right) SeekBy(10_000);
        else if (e.Key == Key.Escape && WindowState == WindowState.FullScreen) ToggleFullScreen();
    }

    private static string Format(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _clock.Stop();
        _hideClock.Stop();
        VideoSurface.MediaPlayer = null;
        _player.Stop();
        _media?.Dispose();
        _player.Dispose();
        _libVlc.Dispose();
    }
}
