using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Pasty.Services;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Pasty.Views;

public sealed partial class AboutWindow : Window
{
    private readonly IntPtr _hwnd;
    private Uri? _latestReleaseUri;

    public AboutWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = Localization.Get("AboutTitle");
        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon(App.IconPath);
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(Scaled(460, 400));
        WindowThemeService.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);
        RootGrid.ActualThemeChanged += (s, e) =>
            WindowThemeService.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);

        AboutIcon.Source = new BitmapImage(new Uri(App.IconPath));
        VersionText.Text = Localization.Format("Version", App.Version);
    }

    public void ApplyTheme(ElementTheme theme) => RootGrid.RequestedTheme = theme;

    private SizeInt32 Scaled(int width, int height)
    {
        var scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        return new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    private async void ProjectLink_Click(object sender, RoutedEventArgs e)
        => await Launcher.LaunchUriAsync(UpdateService.ProjectUri);

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        DownloadButton.Visibility = Visibility.Collapsed;
        UpdateProgress.IsActive = true;
        UpdateStatus.Text = Localization.Get("Checking");
        try
        {
            var result = await UpdateService.CheckAsync();
            _latestReleaseUri = result.ReleaseUri;
            if (result.HasUpdate)
            {
                UpdateStatus.Text = Localization.Format("UpdateFound", result.LatestVersion);
                DownloadButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateStatus.Text = Localization.Format("UpToDate", result.LatestVersion);
            }
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = Localization.Get("UpdateFailed");
            Trace.Log($"检查更新失败: {ex.Message}");
        }
        finally
        {
            UpdateProgress.IsActive = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_latestReleaseUri != null) await Launcher.LaunchUriAsync(_latestReleaseUri);
    }
}
