using System.Windows;

namespace WebHtv.Desktop;

internal static class PlayerWindowLauncher
{
    internal static bool TryShow(Window owner, Func<PlayerWindow> createWindow)
    {
        try
        {
            var window = createWindow();
            window.Owner = owner;
            window.Show();
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                owner,
                $"播放器组件启动失败，程序不会退出。\n\n请重新安装最新版 MT播放器；如果问题仍然存在，请反馈以下信息：\n{exception.Message}",
                "MT播放器",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }
}
