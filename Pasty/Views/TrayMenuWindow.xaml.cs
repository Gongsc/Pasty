using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Pasty.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace Pasty.Views;

/// <summary>由 WinUI 3 绘制的托盘右键菜单窗口。</summary>
public sealed partial class TrayMenuWindow : Window
{
    private const int MenuWidth = 200;
    private const int MenuHeight = 172;

    private readonly Action _open;
    private readonly Action _settings;
    private readonly Action _about;
    private readonly Action _exit;
    private readonly IntPtr _hwnd;

    public TrayMenuWindow(Action open, Action settings, Action about, Action exit)
    {
        InitializeComponent();
        _open = open;
        _settings = settings;
        _about = about;
        _exit = exit;
        _hwnd = WindowNative.GetWindowHandle(this);

        Title = "Pasty";
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetPresenter(OverlappedPresenter.CreateForContextMenu());
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) Close();
        };
    }

    public void ApplyTheme(ElementTheme theme) => RootGrid.RequestedTheme = theme;

    public void ShowAtCursor()
    {
        Win32.GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);

        // 先移到目标屏幕再取 DPI；多屏缩放不一致时，窗口此后得到的才是目标屏的比例。
        AppWindow.Move(new PointInt32(cursor.X, cursor.Y));
        var scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        var size = new SizeInt32(
            (int)Math.Round(MenuWidth * scale),
            (int)Math.Round(MenuHeight * scale));
        AppWindow.Resize(size);

        // 托盘通常在工作区下方，因此优先向左上展开；任务栏置顶或置左时会夹回工作区内。
        var x = FitInto(cursor.X - size.Width, area.WorkArea.X, area.WorkArea.Width, size.Width);
        var y = FitInto(cursor.Y - size.Height, area.WorkArea.Y, area.WorkArea.Height, size.Height);
        AppWindow.Move(new PointInt32(x, y));
        AppWindow.Show(activateWindow: true);
        Activate();
        Win32.ForceForeground(_hwnd);
        OpenItem.Focus(FocusState.Programmatic);
    }

    private static int FitInto(int desired, int origin, int extent, int size)
        => extent <= size ? origin : Math.Clamp(desired, origin, origin + extent - size);

    private void RootGrid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        Close();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _open();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _settings();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _about();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _exit();
    }
}
