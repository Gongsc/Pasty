using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Pasty.Services;

/// <summary>统一自定义标题栏中系统窗口按钮的浅色/深色配色。</summary>
public static class WindowThemeService
{
    public static void ApplyCaptionButtonColors(AppWindow appWindow, ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark;
        var foreground = dark
            ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B);
        var inactiveForeground = dark
            ? Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A)
            : Windows.UI.Color.FromArgb(0xFF, 0x70, 0x70, 0x70);
        var hoverBackground = dark
            ? Windows.UI.Color.FromArgb(0xFF, 0x35, 0x35, 0x35)
            : Windows.UI.Color.FromArgb(0xFF, 0xE5, 0xE5, 0xE5);
        var pressedBackground = dark
            ? Windows.UI.Color.FromArgb(0xFF, 0x42, 0x42, 0x42)
            : Windows.UI.Color.FromArgb(0xFF, 0xD8, 0xD8, 0xD8);

        var titleBar = appWindow.TitleBar;
        var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonBackgroundColor = transparent;
        titleBar.ButtonInactiveBackgroundColor = transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
    }
}
