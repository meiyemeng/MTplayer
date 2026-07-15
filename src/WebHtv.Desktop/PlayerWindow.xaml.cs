using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Globalization;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using WebHtv.Core.Catalogue;
using WebHtv.Core.Configuration;
using WebHtv.Playback;

namespace WebHtv.Desktop;

public partial class PlayerWindow : Window, IDisposable
{
    private readonly NativePlaybackService _playback;
    private readonly ShellViewModel? _viewModel;
    private readonly MovieDetailContext? _context;
    private readonly PlayRequest? _liveRequest;
    private readonly AppSettings _settings;
    private readonly TvBoxParser? _selectedParser;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _controlsHideTimer;
    private int _sourceIndex;
    private int _episodeIndex;
    private long _pendingResumeMs;
    private bool _initializing = true;
    private bool _opening;
    private bool _fullscreen;
    private DateTime _lastHistorySaveUtc = DateTime.MinValue;
    private SkipMarker? _skipMarker;
    private bool _outroTriggered;
    private NativePoint _lastCursorPoint;
    private bool _hasCursorSample;
    private bool _lastMouseButtonDown;
    private double _volumeBeforeMute = 80;
    private WindowStyle _previousStyle;
    private WindowState _previousState;

    internal PlayerWindow(ShellViewModel viewModel, MovieDetailContext context, int sourceIndex, int episodeIndex, long resumeMs, TvBoxParser? selectedParser = null)
    {
        _viewModel = viewModel;
        _context = context;
        _settings = viewModel.Settings;
        _sourceIndex = sourceIndex;
        _episodeIndex = episodeIndex;
        _pendingResumeMs = resumeMs;
        _selectedParser = selectedParser;
        _playback = new NativePlaybackService(_settings.HardwareDecode);
        _uiTimer = CreateTimer();
        _controlsHideTimer = CreateControlsHideTimer();
        InitializeComponent();
        VideoOutput.MediaPlayer = _playback.Player;
    }

    internal PlayerWindow(string title, PlayRequest request, AppSettings settings)
    {
        _settings = settings;
        _liveRequest = request;
        _playback = new NativePlaybackService(settings.HardwareDecode);
        _uiTimer = CreateTimer();
        _controlsHideTimer = CreateControlsHideTimer();
        InitializeComponent();
        TitleText.Text = title;
        VideoOutput.MediaPlayer = _playback.Player;
    }

    internal PlayerWindow(PlayRequest request) : this("MT播放器", request, new AppSettings()) { }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += UiTimer_Tick;
        return timer;
    }

    private DispatcherTimer CreateControlsHideTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += ControlsHideTimer_Tick;
        return timer;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _playback.Player.Playing += Player_Playing;
        _playback.Player.EncounteredError += Player_EncounteredError;
        _playback.Player.EndReached += Player_EndReached;
        _volumeBeforeMute = Math.Max(1, _settings.DefaultVolume);
        VolumeSlider.Value = _settings.DefaultVolume;
        var speedOptions = new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };
        SpeedSelector.ItemsSource = speedOptions;
        SpeedSelector.SelectedItem = speedOptions.OrderBy(value => Math.Abs(value - _settings.DefaultSpeed)).First();
        if (_context is not null)
        {
            TitleText.Text = _context.Card.Title;
            SourceSelector.ItemsSource = _context.Detail.Sources;
            SourceSelector.SelectedIndex = Math.Clamp(_sourceIndex, 0, _context.Detail.Sources.Count - 1);
            PopulateEpisodes();
            EpisodeSelector.SelectedIndex = Math.Clamp(_episodeIndex, 0, Math.Max(0, EpisodeSelector.Items.Count - 1));
            _skipMarker = await _viewModel!.GetSkipMarkerAsync(_context.Card, CurrentLineName());
            UpdateSkipMarkerText();
        }
        else
        {
            SourceSelector.Visibility = Visibility.Collapsed;
            EpisodeSelector.Visibility = Visibility.Collapsed;
            PreviousButton.Visibility = Visibility.Collapsed;
            NextButton.Visibility = Visibility.Collapsed;
            EpisodeText.Text = "直播";
        }
        _initializing = false;
        _uiTimer.Start();
        ShowControls();
        await OpenCurrentAsync();
        if (_settings.AutoFullscreen) ToggleFullscreen();
    }

    private async Task OpenCurrentAsync()
    {
        if (_opening) return;
        _opening = true;
        try
        {
            StatusText.Text = "正在连接播放线路…";
            StatusOverlay.Visibility = Visibility.Visible;
            _outroTriggered = false;
            PlayRequest? request;
            if (_context is not null && _viewModel is not null)
            {
                _sourceIndex = SourceSelector.SelectedIndex;
                _episodeIndex = EpisodeSelector.SelectedIndex;
                if (_sourceIndex < 0 || _episodeIndex < 0) return;
                request = await _viewModel.ResolvePlayRequestAsync(_context, _sourceIndex, _episodeIndex, _selectedParser);
                EpisodeText.Text = $"{_context.Detail.Sources[_sourceIndex].Name} · {_context.Detail.Sources[_sourceIndex].Episodes[_episodeIndex].Name}";
            }
            else request = _liveRequest;
            if (request is null) { StatusText.Text = _viewModel?.StatusMessage ?? "播放地址无效。"; StatusOverlay.Visibility = Visibility.Visible; return; }
            if (request.RequiresParser) { StatusText.Text = "该线路是网页解析线路，请切换到 m3u8/直连线路。"; StatusOverlay.Visibility = Visibility.Visible; return; }
            _playback.Player.Stop();
            await _playback.OpenAsync(request);
        }
        catch (Exception exception) { StatusText.Text = $"播放失败：{exception.Message}"; StatusOverlay.Visibility = Visibility.Visible; }
        finally { _opening = false; }
    }

    private void PopulateEpisodes()
    {
        if (_context is null || SourceSelector.SelectedIndex < 0) return;
        EpisodeSelector.ItemsSource = _context.Detail.Sources[SourceSelector.SelectedIndex].Episodes;
    }

    private void Player_Playing(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _playback.Player.Volume = (int)VolumeSlider.Value;
        _playback.Player.SetRate((float)(SpeedSelector.SelectedItem is double rate ? rate : 1.0));
        if (_pendingResumeMs > 0) { _playback.Player.Time = _pendingResumeMs; _pendingResumeMs = 0; }
        else if (_skipMarker is { IntroEndMs: > 0 }) _playback.Player.Time = _skipMarker.IntroEndMs;
        StatusOverlay.Visibility = Visibility.Collapsed;
        PlayPauseButton.Content = "暂停";
    });

    private void Player_EncounteredError(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        StatusText.Text = "播放失败：当前线路无法连接，请切换线路或重试。";
        StatusOverlay.Visibility = Visibility.Visible;
    });

    private void Player_EndReached(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_context is not null && _episodeIndex + 1 < _context.Detail.Sources[_sourceIndex].Episodes.Count)
        {
            EpisodeSelector.SelectedIndex = _episodeIndex + 1;
        }
    });

    private async void UiTimer_Tick(object? sender, EventArgs e)
    {
        DetectNativeMouseActivity();
        var time = Math.Max(0, _playback.Player.Time);
        var length = Math.Max(0, _playback.Player.Length);
        if (!PositionSlider.IsMouseCaptureWithin)
        {
            PositionSlider.Maximum = Math.Max(1, length);
            PositionSlider.Value = Math.Min(time, PositionSlider.Maximum);
        }
        CurrentTimeText.Text = FormatTime(time);
        DurationText.Text = FormatTime(length);
        PlayPauseButton.Content = _playback.Player.IsPlaying ? "暂停" : "播放";
        if (!_outroTriggered && _skipMarker is { OutroStartMs: > 0 } marker && time >= marker.OutroStartMs)
        {
            _outroTriggered = true;
            if (_context is not null && EpisodeSelector.SelectedIndex >= 0 && EpisodeSelector.SelectedIndex < EpisodeSelector.Items.Count - 1)
                EpisodeSelector.SelectedIndex++;
            else
                _playback.Player.Pause();
        }
        if (_context is not null && time > 0 && DateTime.UtcNow - _lastHistorySaveUtc >= TimeSpan.FromSeconds(15))
        {
            _lastHistorySaveUtc = DateTime.UtcNow;
            await SaveProgressAsync();
        }
    }

    private static string FormatTime(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(milliseconds >= 3_600_000 ? @"hh\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
    private void PositionSlider_MouseUp(object sender, MouseButtonEventArgs e) => _playback.Player.Time = (long)PositionSlider.Value;
    private void PositionSlider_KeyUp(object sender, KeyEventArgs e) => _playback.Player.Time = (long)PositionSlider.Value;
    private void PlayPause_Click(object sender, RoutedEventArgs e) { if (_playback.Player.IsPlaying) _playback.Player.Pause(); else _playback.Player.Play(); }
    private void Back10_Click(object sender, RoutedEventArgs e) => _playback.Player.Time = Math.Max(0, _playback.Player.Time - 10_000);
    private void Forward10_Click(object sender, RoutedEventArgs e) => _playback.Player.Time = Math.Min(_playback.Player.Length, _playback.Player.Time + 10_000);
    private async void SetIntro_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _context is null) return;
        var intro = Math.Max(0, _playback.Player.Time);
        var outro = _skipMarker?.OutroStartMs ?? 0;
        if (outro > 0 && intro >= outro) { SkipMarkerText.Text = "片头点必须早于片尾点"; return; }
        await _viewModel.SaveSkipMarkerAsync(
            _context.Card,
            CurrentLineName(),
            intro,
            outro,
            _playback.Player.Length);
        _skipMarker = new SkipMarker(_context.Card.SourceKey, _context.Card.Id, intro, outro);
        UpdateSkipMarkerText();
    }

    private async void SetOutro_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _context is null) return;
        var intro = _skipMarker?.IntroEndMs ?? 0;
        var outro = Math.Max(0, _playback.Player.Time);
        if (outro <= intro) { SkipMarkerText.Text = "片尾点必须晚于片头点"; return; }
        await _viewModel.SaveSkipMarkerAsync(
            _context.Card,
            CurrentLineName(),
            intro,
            outro,
            _playback.Player.Length);
        _skipMarker = new SkipMarker(_context.Card.SourceKey, _context.Card.Id, intro, outro);
        UpdateSkipMarkerText();
    }

    private async void ClearSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _context is null) return;
        await _viewModel.SaveSkipMarkerAsync(
            _context.Card,
            CurrentLineName(),
            0,
            0,
            _playback.Player.Length);
        _skipMarker = null;
        UpdateSkipMarkerText();
    }

    private void UpdateSkipMarkerText()
    {
        SkipMarkerText.Text = _skipMarker is null
            ? "未设置"
            : $"片头 {FormatTime(_skipMarker.IntroEndMs)} / 片尾 {FormatTime(_skipMarker.OutroStartMs)}";
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (EpisodeSelector.SelectedIndex <= 0) return;
        EpisodeSelector.SelectedIndex--;
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (EpisodeSelector.SelectedIndex < 0 || EpisodeSelector.SelectedIndex >= EpisodeSelector.Items.Count - 1) return;
        EpisodeSelector.SelectedIndex++;
    }

    private async void SourceSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing || SourceSelector.SelectedIndex < 0) return;
        _sourceIndex = SourceSelector.SelectedIndex;
        _initializing = true;
        PopulateEpisodes();
        EpisodeSelector.SelectedIndex = Math.Clamp(_episodeIndex, 0, Math.Max(0, EpisodeSelector.Items.Count - 1));
        _initializing = false;
        _skipMarker = await _viewModel!.GetSkipMarkerAsync(_context!.Card, CurrentLineName());
        UpdateSkipMarkerText();
        await OpenCurrentAsync();
    }

    private string CurrentLineName() =>
        _context is not null && _sourceIndex >= 0 && _sourceIndex < _context.Detail.Sources.Count
            ? _context.Detail.Sources[_sourceIndex].Name
            : string.Empty;

    private async void EpisodeSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing || EpisodeSelector.SelectedIndex < 0) return;
        _episodeIndex = EpisodeSelector.SelectedIndex;
        _pendingResumeMs = 0;
        await OpenCurrentAsync();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_playback is not null) _playback.Player.Volume = (int)e.NewValue;
        if (e.NewValue > 0) _volumeBeforeMute = e.NewValue;
        if (MuteButton is not null) MuteButton.Content = e.NewValue <= 0 ? "恢复声音" : "静音";
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeSlider.Value > 0)
        {
            _volumeBeforeMute = VolumeSlider.Value;
            VolumeSlider.Value = 0;
        }
        else
        {
            VolumeSlider.Value = Math.Clamp(_volumeBeforeMute, 1, 100);
        }
    }
    private void SpeedSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (SpeedSelector.SelectedItem is double rate) _playback.Player.SetRate((float)rate); }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _previousStyle = WindowStyle; _previousState = WindowState;
            WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; FullscreenButton.Content = "退出全屏";
        }
        else { WindowStyle = _previousStyle; WindowState = _previousState; FullscreenButton.Content = "全屏"; }
        _fullscreen = !_fullscreen;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        ShowControls();
        if (e.Key == Key.Space) PlayPause_Click(sender, e);
        else if (e.Key == Key.Left) Back10_Click(sender, e);
        else if (e.Key == Key.Right) Forward10_Click(sender, e);
        else if (e.Key == Key.M) Mute_Click(sender, e);
        else if (e.Key == Key.F11) ToggleFullscreen();
        else if (e.Key == Key.Escape && _fullscreen) ToggleFullscreen();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e) => ShowControls();

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e) => ShowControls();

    private void ShowControls()
    {
        if (!IsLoaded) return;
        Cursor = Cursors.Arrow;
        TopOverlay.IsHitTestVisible = true;
        ControlOverlay.IsHitTestVisible = true;
        AnimateOverlay(TopOverlay, 1);
        AnimateOverlay(ControlOverlay, 1);
        _controlsHideTimer.Stop();
        _controlsHideTimer.Start();
    }

    private void ControlsHideTimer_Tick(object? sender, EventArgs e)
    {
        if (PositionSlider.IsMouseCaptureWithin || VolumeSlider.IsMouseCaptureWithin ||
            SourceSelector.IsDropDownOpen || EpisodeSelector.IsDropDownOpen || SpeedSelector.IsDropDownOpen)
        {
            ShowControls();
            return;
        }

        _controlsHideTimer.Stop();
        TopOverlay.IsHitTestVisible = false;
        ControlOverlay.IsHitTestVisible = false;
        AnimateOverlay(TopOverlay, 0);
        AnimateOverlay(ControlOverlay, 0);
        Cursor = Cursors.None;
    }

    private static void AnimateOverlay(UIElement overlay, double opacity)
    {
        overlay.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = opacity,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void DetectNativeMouseActivity()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetCursorPos(out var point) || !GetWindowRect(handle, out var bounds)) return;

        var isInside = point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
        var isMouseDown = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
        var moved = _hasCursorSample && (point.X != _lastCursorPoint.X || point.Y != _lastCursorPoint.Y);
        var clicked = isMouseDown && !_lastMouseButtonDown;
        if (isInside && (moved || clicked)) ShowControls();

        _lastCursorPoint = point;
        _hasCursorSample = true;
        _lastMouseButtonDown = isMouseDown;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private async Task SaveProgressAsync()
    {
        if (_viewModel is null || _context is null || _sourceIndex < 0 || _episodeIndex < 0) return;
        await _viewModel.SaveHistoryAsync(_context.Card, _sourceIndex, _episodeIndex, _playback.Player.Time, _playback.Player.Length);
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        await SaveProgressAsync();
        Dispose();
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        _controlsHideTimer.Stop();
        _playback.Player.Playing -= Player_Playing;
        _playback.Player.EncounteredError -= Player_EncounteredError;
        _playback.Player.EndReached -= Player_EndReached;
        _playback.Dispose();
        GC.SuppressFinalize(this);
    }
}
