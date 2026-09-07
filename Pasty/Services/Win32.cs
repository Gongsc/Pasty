using System.Runtime.InteropServices;
using System.Text;

namespace Pasty.Services;

internal static class Win32
{
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr KeyboardHookDelegate(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>SetWinEventHook 的回调签名（OUT_OFCONTEXT 模式下由安装线程的消息泵驱动）。</summary>
    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        uint idObject, uint idChild, uint dwEventThread, uint msEventTime);

    public const uint WM_CLIPBOARDUPDATE = 0x031D;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_APP_TRAY = 0x8100;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_NULL = 0x0000;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYUP = 0x0105;
    public const int WH_KEYBOARD_LL = 13;
    public const int LLKHF_INJECTED = 0x10;
    public const uint VK_CONTROL = 0x11;
    public const uint VK_SHIFT = 0x10;
    public const uint VK_MENU = 0x12; // Alt
    public const uint VK_V = 0x56;

    /// <summary>不进任务栏、不进 Alt+Tab。承载消息的顶层窗口必须带上，否则会露出一个空窗口按钮。</summary>
    public const uint WS_EX_TOOLWINDOW = 0x0080;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, KeyboardHookDelegate lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(uint vKey);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentProcessId();

    /// <summary>
    /// 该窗口能否作为粘贴目标：句柄仍然有效、可见，且不属于本进程。
    /// 缓存下来的前台句柄可能已被销毁并回收给别的进程，此时强切前台后
    /// SendInput 会把剪贴板内容送进一个无关窗口，必须先校验。
    /// </summary>
    public static bool IsPasteTarget(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd) || !IsWindowVisible(hWnd)) return false;
        GetWindowThreadProcessId(hWnd, out var pid);
        return pid != 0 && pid != GetCurrentProcessId();
    }

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>可靠地把目标窗口带到前台（后台进程直接调 SetForegroundWindow 会被系统拒绝）。</summary>
    public static void ForceForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || hWnd == GetForegroundWindow()) return;
        var targetThread = GetWindowThreadProcessId(hWnd, out _);
        var currentThread = GetCurrentThreadId();
        AttachThreadInput(currentThread, targetThread, true);
        SetForegroundWindow(hWnd);
        AttachThreadInput(currentThread, targetThread, false);
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>窗口所在显示器的 DPI（96 = 100%）。AppWindow 的坐标与尺寸都是物理像素，
    /// 逻辑尺寸必须按此换算，否则在高 DPI 屏上窗口会小得没法用。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi; // x64 上联合体必须按最大的 MOUSEINPUT 对齐，总大小 40 字节
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    // 托盘
    [DllImport("shell32.dll", SetLastError = true)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    public const uint NIM_ADD = 0x0, NIM_MODIFY = 0x1, NIM_DELETE = 0x2;
    public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterClipboardFormatW(string lpszFormat);

    public const uint CF_DIB = 8;
    public const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    public static extern bool CloseClipboard();

    /// <summary>剪贴板内容每变化一次该序号 +1，用于识别“这次变化是我们自己造成的”。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>把字节数组以指定剪贴板格式写入（需在 OpenClipboard 之后调用）。失败返回 false。</summary>
    public static bool SetClipboardBytes(uint format, byte[] data)
    {
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (hMem == IntPtr.Zero) return false;
        var p = GlobalLock(hMem);
        if (p == IntPtr.Zero) { GlobalFree(hMem); return false; }
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(hMem);
        if (SetClipboardData(format, hMem) == IntPtr.Zero)
        {
            GlobalFree(hMem); // 系统未接管内存，自行释放
            return false;
        }
        return true; // 成功后内存归系统所有
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    public const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800;

    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    public const uint TPM_LEFTALIGN = 0x0, TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    /// <summary>
    /// 从 .ico 文件加载指定像素尺寸的图标（hInstance 传 IntPtr.Zero + LR_LOADFROMFILE）。
    /// 用它而不是 LoadIconW：LoadIconW 只会取 SM_CXICON（通常 32）那一档再缩放，
    /// 而托盘要的是 SM_CXSMICON（100% DPI 下 16）——缩放出来的 16px 是一团糊。
    /// 返回的句柄归调用方所有，用完必须 DestroyIcon。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x00000010;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    /// <summary>托盘图标的目标尺寸：随 DPI 变化，100% = 16，125% = 20，150% = 24，200% = 32。</summary>
    public const int SM_CXSMICON = 49, SM_CYSMICON = 50;

    /// <summary>lParam 为 "ImmersiveColorSet" 时表示系统主题（含任务栏明暗）变了。</summary>
    public const uint WM_SETTINGCHANGE = 0x001A;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    public static uint SendCtrlV()
    {
        // 带扫描码发送，兼容按扫描码处理按键的应用
        var inputs = new INPUT[4];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = (ushort)VK_CONTROL;
        inputs[0].u.ki.wScan = 0x001D;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = (ushort)VK_V;
        inputs[1].u.ki.wScan = 0x002F;
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].u.ki.wVk = (ushort)VK_V;
        inputs[2].u.ki.wScan = 0x002F;
        inputs[2].u.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].u.ki.wVk = (ushort)VK_CONTROL;
        inputs[3].u.ki.wScan = 0x001D;
        inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    /// <summary>返回前台线程当前拥有键盘焦点的窗口类名，用于诊断。</summary>
    public static string GetFocusInfo()
    {
        var fg = GetForegroundWindow();
        var tid = GetWindowThreadProcessId(fg, out _);
        var info = new GUITHREADINFO();
        info.cbSize = Marshal.SizeOf<GUITHREADINFO>();
        if (!GetGUIThreadInfo(tid, ref info)) return "GetGUIThreadInfo失败";
        var sb = new StringBuilder(256);
        GetClassNameW(info.hwndFocus != IntPtr.Zero ? info.hwndFocus : fg, sb, 256);
        return sb.ToString();
    }

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    /// <summary>
    /// 订阅系统事件（这里只要前台窗口切换）。hModWinEventProc 与 idProcess/idThread 全留默认值
    /// 表示“全局、所有进程”，OUT_OFCONTEXT 标志表示回调在自己的线程上被调用，不注入别人进程。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WNDCLASSW
{
    public uint style;
    public Win32.WndProcDelegate lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public IntPtr hInstance;
    public IntPtr hIcon;
    public IntPtr hCursor;
    public IntPtr hbrBackground;
    public string? lpszMenuName;
    public string lpszClassName;
}
