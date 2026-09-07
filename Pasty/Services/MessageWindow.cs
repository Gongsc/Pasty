namespace Pasty.Services;

using System.Runtime.InteropServices;

/// <summary>
/// UI 线程上的 Win32 消息窗口：承载剪贴板监听、热键 WM_HOTKEY 与托盘回调消息，
/// 消息由 XAML 消息循环泵出，回调天然位于 UI 线程。
///
/// 是一个 0×0、从不 ShowWindow 的顶层窗口，而不是 HWND_MESSAGE 消息专用窗口。
/// 消息专用窗口有两条致命限制：收不到 RegisterWindowMessage 那类广播（于是
/// Explorer 重启后重建托盘图标的 TaskbarCreated 永远不会到，图标一去不返），
/// 也永远无法成为前台窗口（于是托盘右键菜单点击外部不消失、按 Esc 也不关）。
/// WS_EX_TOOLWINDOW 保证它不出现在任务栏和 Alt+Tab 里。
/// </summary>
public sealed class MessageWindow : IDisposable
{
    /// <summary>类名对外可见：第二个实例靠它 FindWindow 找到已运行实例的消息窗口。</summary>
    public const string ClassName = "Pasty_MsgWindow";
    private static bool s_classRegistered;
    private static MessageWindow? s_instance;

    /// <summary>
    /// 窗口类持有的是这个委托的裸函数指针，托管侧必须自己留一份强引用。
    /// 原先直接把方法组赋给 lpfnWndProc，委托只在 RegisterClassW 期间存活，
    /// 随后就可被 GC 回收：之后任意一条消息（复制、按热键、点托盘）到达时
    /// 调用的是已回收的存根，表现为随机时刻的进程闪退。
    /// </summary>
    private static readonly Win32.WndProcDelegate WndProcThunk = WndProc;

    public event Func<uint, IntPtr, IntPtr, bool>? ProcessMessage; // 返回 true 表示已处理

    public IntPtr Handle { get; }

    public MessageWindow()
    {
        if (!s_classRegistered)
        {
            var wc = new WNDCLASSW
            {
                lpfnWndProc = WndProcThunk,
                hInstance = Win32.GetModuleHandleW(null),
                lpszClassName = ClassName,
            };
            Win32.RegisterClassW(ref wc);
            s_classRegistered = true;
        }
        // 顶层但无 WS_VISIBLE：始终不可见，同时保留接收广播消息与成为前台窗口的能力
        Handle = Win32.CreateWindowExW(Win32.WS_EX_TOOLWINDOW, ClassName, "Pasty", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException($"创建消息窗口失败（错误码 {Marshal.GetLastWin32Error()}），热键/剪贴板监听/托盘将不可用");
        s_instance = this;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_COMMAND || s_instance?.ProcessMessage?.Invoke(msg, wParam, lParam) == true)
            return IntPtr.Zero;
        return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose() => s_instance = null;
}
