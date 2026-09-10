using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Pasty.Views;

namespace Pasty.Services;

/// <summary>系统托盘图标：双击打开面板，右键菜单（打开/设置/关于/退出）。</summary>
public sealed class TrayIconService
{
    private static IntPtr s_hwnd;
    private static bool s_added;
    private static uint s_taskbarCreatedMsg;

    /// <summary>当前托盘图标句柄。</summary>
    private static IntPtr s_icon;
    /// <summary>句柄是否由我们加载。false 表示退化到了系统共享图标，绝不能销毁。</summary>
    private static bool s_iconOwned;
    /// <summary>s_icon 取自哪一套配色，用于判断系统主题变化后是否需要换图。</summary>
    private static bool s_iconForLightTheme;
    private static TrayMenuWindow? s_menuWindow;

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? AboutRequested;
    public event Action? ExitRequested;

    public TrayIconService(MessageWindow messageWindow)
    {
        messageWindow.ProcessMessage += OnMessage;
        s_hwnd = messageWindow.Handle;
        s_taskbarCreatedMsg = Win32.RegisterWindowMessageW("TaskbarCreated");
        Add();
    }

    /// <summary>
    /// 任务栏是浅色还是深色。Windows 不会替第三方托盘图标做反色，
    /// 单色一版必然在某一端糊掉，所以浅/深两套图标都要有，按这个值挑。
    /// </summary>
    private static bool IsLightTaskbar()
        => Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 1) is int v ? v != 0 : true;

    /// <summary>
    /// 按当前任务栏配色和 DPI 加载托盘图标。失败时退回系统图标并标记为“非自有”。
    /// 尺寸取 SM_CXSMICON 而不是固定 16：125% DPI 下托盘要 20px，
    /// 传 16 会让系统拉伸，边缘全是半透明灰。
    /// </summary>
    private static void LoadIcon()
    {
        var light = IsLightTaskbar();
        var file = Path.Combine(AppContext.BaseDirectory, "Assets",
            light ? "Tray-light.ico" : "Tray-dark.ico");
        var cx = Win32.GetSystemMetrics(Win32.SM_CXSMICON);
        var cy = Win32.GetSystemMetrics(Win32.SM_CYSMICON);

        var h = Win32.LoadImageW(IntPtr.Zero, file, Win32.IMAGE_ICON, cx, cy, Win32.LR_LOADFROMFILE);
        var owned = h != IntPtr.Zero;
        if (!owned)
        {
            Trace.Log($"托盘图标加载失败 err={Marshal.GetLastWin32Error()} file={file}");
            h = Win32.LoadIconW(IntPtr.Zero, new IntPtr(32512) /* IDI_APPLICATION */);
        }
        s_icon = h;
        s_iconOwned = owned;
        s_iconForLightTheme = light;
    }

    /// <summary>销毁自有图标句柄；系统共享图标只清引用。</summary>
    private static void FreeIcon(IntPtr icon, bool owned)
    {
        if (owned && icon != IntPtr.Zero) Win32.DestroyIcon(icon);
    }

    private static void Add()
    {
        // Explorer 重启后会再进来一次，旧句柄先放掉，否则每次重启漏一个
        FreeIcon(s_icon, s_iconOwned);
        LoadIcon();

        var data = new Win32.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
            hWnd = s_hwnd,
            uID = 1,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP,
            uCallbackMessage = Win32.WM_APP_TRAY,
            hIcon = s_icon,
            szTip = "Pasty 剪切板管理器",
        };
        s_added = Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref data);
    }

    /// <summary>
    /// 系统主题变化后换图。只在明暗真的翻转时动手——ImmersiveColorSet 也会因为
    /// 强调色之类的改动广播，每次都重载纯属浪费。
    /// </summary>
    private static void RefreshIcon()
    {
        if (!s_added || IsLightTaskbar() == s_iconForLightTheme) return;

        var (oldIcon, oldOwned) = (s_icon, s_iconOwned);
        LoadIcon();

        var data = new Win32.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
            hWnd = s_hwnd,
            uID = 1,
            uFlags = Win32.NIF_ICON,
            hIcon = s_icon,
        };
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
        // Shell_NotifyIcon 已经把图标复制走了，这时才能销毁旧句柄。
        // 换在 NIM_MODIFY 之前销毁，托盘会短暂持有废句柄；干脆不销毁则每切换一次主题漏一个。
        FreeIcon(oldIcon, oldOwned);
    }

    public static void Remove()
    {
        s_menuWindow?.Close();
        s_menuWindow = null;
        if (!s_added) return;
        var data = new Win32.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
            hWnd = s_hwnd,
            uID = 1,
        };
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
        s_added = false;
        // 图标是 LoadImage 从自己的 .ico 加载的，句柄归我们所有，必须销毁。
        // 但退化到 LoadIcon(IDI_APPLICATION) 那条路上的句柄是全进程共用的系统图标，
        // 销毁它会让其他还在用的地方（以及下次 Explorer 重启后的 Add）拿到废句柄——
        // 两种来源的区别就记在 s_iconOwned 上。
        FreeIcon(s_icon, s_iconOwned);
        s_icon = IntPtr.Zero;
        s_iconOwned = false;
    }

    private bool OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (s_taskbarCreatedMsg != 0 && msg == s_taskbarCreatedMsg)
        {
            // Explorer 重启后托盘被清空，重新添加
            Add();
            return true;
        }
        if (msg == Win32.WM_SETTINGCHANGE)
        {
            // 任务栏明暗切换靠这条广播通知，lParam 是区域名字符串
            if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                RefreshIcon();
            return false; // 其他人也要看这条广播，不吞
        }
        if (msg != Win32.WM_APP_TRAY) return false;
        var mouse = (uint)lParam.ToInt64() & 0xFFFF;
        if (mouse == 0x0203 /* WM_LBUTTONDBLCLK */)
            OpenRequested?.Invoke();
        else if (mouse == 0x0205 /* WM_RBUTTONUP */)
            ShowMenu();
        return true;
    }

    private void ShowMenu()
    {
        s_menuWindow?.Close();
        var menu = new TrayMenuWindow(
            () => OpenRequested?.Invoke(),
            () => SettingsRequested?.Invoke(),
            () => AboutRequested?.Invoke(),
            () => ExitRequested?.Invoke());
        s_menuWindow = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(s_menuWindow, menu)) s_menuWindow = null;
        };
        menu.ApplyTheme(CurrentTheme());
        menu.ShowAtCursor();
    }

    public static void ApplyTheme(ElementTheme theme) => s_menuWindow?.ApplyTheme(theme);

    private static ElementTheme CurrentTheme() => App.Settings.Theme switch
    {
        1 => ElementTheme.Light,
        2 => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };
}
