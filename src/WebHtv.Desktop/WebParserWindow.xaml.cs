using System.Windows;
using Microsoft.Web.WebView2.Core;
using WebHtv.Core.Catalogue;

namespace WebHtv.Desktop;

public partial class WebParserWindow : Window
{
    private readonly PlayRequest _request;
    private int _mediaCaptured;

    public WebParserWindow(PlayRequest request)
    {
        _request = request;
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.WebResourceResponseReceived += WebResourceResponseReceived;
            Browser.NavigationCompleted += (_, _) => LoadingPanel.Visibility = Visibility.Collapsed;
            Browser.Source = new Uri(_request.Url);
        }
        catch (Exception exception)
        {
            LoadingPanel.Visibility = Visibility.Visible;
            ((System.Windows.Controls.TextBlock)((System.Windows.Controls.StackPanel)LoadingPanel.Child).Children[0]).Text = $"解析页启动失败：{exception.Message}";
        }
    }

    private async void WebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (Volatile.Read(ref _mediaCaptured) != 0 || !LooksLikeMedia(e.Request.Uri, e.Response.Headers)) return;
        if (Interlocked.Exchange(ref _mediaCaptured, 1) != 0) return;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = _request.Url
        };
        try
        {
            var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(e.Request.Uri);
            if (cookies.Count > 0) headers["Cookie"] = string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
        }
        catch (Exception)
        {
            // Some cross-origin responses do not expose cookies; the media URL may still be public.
        }

        await Dispatcher.InvokeAsync(() =>
        {
            new PlayerWindow(new PlayRequest(e.Request.Uri, _request.Flag, false, headers)).Show();
            Close();
        });
    }

    private static bool LooksLikeMedia(string address, CoreWebView2HttpResponseHeaders headers)
    {
        var path = address.Split('?', 2)[0];
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".flv", StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            var contentType = headers.GetHeader("Content-Type");
            return contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
                   contentType.Contains("video/", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
