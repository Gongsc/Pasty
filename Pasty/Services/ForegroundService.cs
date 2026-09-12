using System.Runtime.InteropServices;
using System.Text;

namespace Pasty.Services;

/// <summary>
/// 持续记录"最近一个不属于本进程的前台窗口"，作为粘贴目标的兜底来源。
///
/// 为什么需要它：粘贴目标以前只在热键触发的那一刻记一次
/// （<see cref="HotkeyService.ConsumeLastForegroundWindow"/>），而鼠标操作——双击条目、
/// 点"粘贴"按钮——压根没有那样的时刻，只能退回 GetForegroundWindow()，
/// 拿到的却是 Pasty 自己的窗口，于是只写剪贴板、不发送按键，
/// 界面上的表现就是"条目跳到了最顶端，别的应用里什么都没出现"。
/// 前台窗口切换是系统事件，订阅它就能在任何时候知道用户刚从哪个应用离开。
/// </summary>
public static class ForegroundService
{
    /// <summary>窗口类持有裸函数指针，托管侧必须留强引用，否则回调到达时存根已被 GC 回收。</summary>
    private static Win32.WinEventDelegate? _thunk;
    private static IntPtr _hook;
    private static IntPtr _last;

    /// <summary>
    /// 最近的外部前台窗口。每次前台切换都会刷新，所以它比"触发时记一次"更不容易过期；
    /// 读的时候仍然要过一遍 IsPasteTarget——那个窗口可能早就被用户关了。
    /// </summary>
    public static IntPtr LastWindow
    {
        get
        {
            var h = _last;
            return Win32.IsPasteTarget(h) ? h : IntPtr.Zero;
        }
    }

    public static void Start()
    {
        _thunk = OnWinEvent;
        _hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _thunk, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        if (_hook == IntPtr.Zero)
            Trace.Log($"SetWinEventHook 失败 err={Marshal.GetLastWin32Error()}，鼠标粘贴将只能写剪贴板");
        // 补记一次当前前台：开机自启时 Pasty 一起来就在前台，之后用户切走才会触发事件，
        // 中间这段时间里 LastWindow 会是空的
        Note(Win32.GetForegroundWindow());
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) Win32.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _last = IntPtr.Zero;
    }

    /// <summary>
    /// 解析这一次粘贴的目标：先取热键触发时记下的句柄（那一刻最准），
    /// 再退回持续跟踪到的外部窗口。都拿不到就交给 PasteService 校验，
    /// 它会在没有有效目标时只写剪贴板、不发按键。
    /// </summary>
    public static IntPtr ResolvePasteTarget()
    {
        var target = HotkeyService.ConsumeLastForegroundWindow();
        if (Win32.IsPasteTarget(target)) return target;
        target = LastWindow;
        return Win32.IsPasteTarget(target) ? target : Win32.GetForegroundWindow();
    }

    private static void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        uint idObject, uint idChild, uint dwEventThread, uint msEventTime)
    {
        // 回调跑在 UI 线程的消息泵上，和键盘钩子一样不许做耗时事：只记一个句柄
        Note(hwnd);
    }

    private static void Note(IntPtr hwnd)
    {
        // 自己的窗口（主窗口、设置窗口）不覆盖记录：主窗口显示之后，
        // 用户"刚从哪儿离开"仍然是那个外部应用
        if (!Win32.IsPasteTarget(hwnd)) return;
        if (IsShellWindow(hwnd)) return;
        _last = hwnd;
    }

    /// <summary>
    /// 任务栏与桌面这类"不是任何应用"的窗口。把它们当目标，Ctrl+V 会石沉大海，
    /// 还不如保留上一个真正的应用窗口。
    /// </summary>
    private static bool IsShellWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        if (Win32.GetClassNameW(hwnd, sb, sb.Capacity) == 0) return false;
        return sb.ToString() switch
        {
            "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" => true,
            _ => false,
        };
    }
}
