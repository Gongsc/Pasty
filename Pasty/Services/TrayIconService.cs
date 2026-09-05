using System.Runtime.InteropServices;

namespace Pasty.Services;

/// <summary>系统托盘图标：双击打开面板，右键菜单（打开/设置/退出）。</summary>
public sealed class TrayIconService
{
    private const uint IdOpen = 1, IdSettings = 2, IdExit = 3;

    private static IntPtr s_icon;
    private static IntPtr s_hwnd;
    private static bool s_added;
    private static uint s_taskbarCreatedMsg;

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayIconService(MessageWindow messageWindow)
    {
        messageWindow.ProcessMessage += OnMessage;
        s_hwnd = messageWindow.Handle;
        s_taskbarCreatedMsg = Win32.RegisterWindowMessageW("TaskbarCreated");
        Add();
    }

    private static void Add()
    {
        var data = new Win32.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
            hWnd = s_hwnd,
            uID = 1,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP,
            uCallbackMessage = Win32.WM_APP_TRAY,
            hIcon = Win32.LoadIconW(IntPtr.Zero, new IntPtr(32512) /* IDI_APPLICATION */),
            szTip = "Pasty 剪切板管理器",
        };
        s_icon = data.hIcon;
        s_added = Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref data);
    }

    public static void Remove()
    {
        if (!s_added) return;
        var data = new Win32.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
            hWnd = s_hwnd,
            uID = 1,
        };
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
        s_added = false;
        if (s_icon != IntPtr.Zero) Win32.DestroyIcon(s_icon);
    }

    private bool OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (s_taskbarCreatedMsg != 0 && msg == s_taskbarCreatedMsg)
        {
            // Explorer 重启后托盘被清空，重新添加
            Add();
            return true;
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
        var menu = Win32.CreatePopupMenu();
        Win32.AppendMenuW(menu, Win32.MF_STRING, new IntPtr(IdOpen), "打开面板");
        Win32.AppendMenuW(menu, Win32.MF_STRING, new IntPtr(IdSettings), "设置");
        Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, IntPtr.Zero, "");
        Win32.AppendMenuW(menu, Win32.MF_STRING, new IntPtr(IdExit), "退出");
        Win32.GetCursorPos(out var p);
        var cmd = Win32.TrackPopupMenu(menu, Win32.TPM_LEFTALIGN | Win32.TPM_RETURNCMD, p.X, p.Y, 0, App.MessageWindow.Handle, IntPtr.Zero);
        Win32.DestroyMenu(menu);
        switch ((uint)cmd)
        {
            case IdOpen: OpenRequested?.Invoke(); break;
            case IdSettings: SettingsRequested?.Invoke(); break;
            case IdExit: ExitRequested?.Invoke(); break;
        }
    }
}
