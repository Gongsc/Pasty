using System.Runtime.InteropServices;

namespace Pasty.Services;

/// <summary>系统托盘图标：双击打开面板，右键菜单（打开/设置/退出）。</summary>
public sealed class TrayIconService
{
    private const uint IdOpen = 1, IdSettings = 2, IdExit = 3;

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
        // 图标来自 LoadIcon(IDI_APPLICATION)，是系统共享图标，由系统自己管理生命周期。
        // 原先在这里 DestroyIcon：句柄不属于我们，销毁的是全进程共用的那一份，
        // 其他还在用它的地方（以及下次 Explorer 重启后的 Add）就拿到了废句柄。
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

        // 菜单的消息循环只服务于前台窗口：owner 不在前台时，点击别处或按 Esc
        // 都不会让菜单消失，它会一直挂在屏幕上，直到再点它自己一次。
        var owner = App.MessageWindow.Handle;
        Win32.ForceForeground(owner);
        var cmd = Win32.TrackPopupMenu(menu,
            Win32.TPM_LEFTALIGN | Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD,
            p.X, p.Y, 0, owner, IntPtr.Zero);
        // 菜单收起后 owner 还欠一条消息才肯把菜单模式彻底退干净（否则下一次右键
        // 首次点击会被吞掉），投一条空消息把消息循环踢一下即可
        Win32.PostMessageW(owner, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        Win32.DestroyMenu(menu);
        switch ((uint)cmd)
        {
            case IdOpen: OpenRequested?.Invoke(); break;
            case IdSettings: SettingsRequested?.Invoke(); break;
            case IdExit: ExitRequested?.Invoke(); break;
        }
    }
}
