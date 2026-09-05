using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Pasty.Services;
/// <summary>
/// 全局热键（RegisterHotKey）+ 低级键盘钩子（覆盖系统 Ctrl+V）。
/// 钩子回调内不做任何耗时工作，仅入队到 UI 线程，避免超时被系统摘除。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int IdShowPanel = 1;
    private const int IdPasteTop = 2;

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private IntPtr _hook;
    private readonly Win32.KeyboardHookDelegate _hookProc; // 防 GC
    private bool _vKeyDown; // 物理 V 键是否处于按下状态（用于忽略自动重复）
    public static volatile bool OverrideCtrlV;
    public static volatile bool SuppressHookAction;

    /// <summary>诊断开关：存在 %LOCALAPPDATA%\Pasty\hooktest 文件时，注入按键也走物理键路径。</summary>
    public static readonly bool HookTestMode = File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pasty", "hooktest"));

    /// <summary>触发热键（或 Ctrl+V 拦截）时的前台窗口。只经 ConsumeLastForegroundWindow 读取。</summary>
    private static IntPtr s_lastForegroundWindow;

    /// <summary>
    /// 取出并清除缓存的前台句柄。该值只对紧随触发之后的那一次粘贴有效：
    /// 留着不清会让几小时前按热键时记下的窗口继续被当成粘贴目标，
    /// 而那个句柄可能早已销毁并被回收给别的进程。
    /// 因此不提供只读属性——一次非消费式读取就是这个 bug 本身。
    /// </summary>
    public static IntPtr ConsumeLastForegroundWindow()
        => Interlocked.Exchange(ref s_lastForegroundWindow, IntPtr.Zero);

    public event Action? ShowPanelRequested;
    public event Action? PasteTopRequested;

    public HotkeyService(MessageWindow messageWindow)
    {
        _hwnd = messageWindow.Handle;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        messageWindow.ProcessMessage += OnMessage;
        _hookProc = LowLevelHook;
        InstallHook();
        ReRegister();
    }

    public void ReRegister()
    {
        Win32.UnregisterHotKey(_hwnd, IdShowPanel);
        Win32.UnregisterHotKey(_hwnd, IdPasteTop);
        Win32.RegisterHotKey(_hwnd, IdShowPanel, App.Settings.ShowHotkeyModifiers, App.Settings.ShowHotkeyVk);
        Win32.RegisterHotKey(_hwnd, IdPasteTop, App.Settings.PasteTopHotkeyModifiers, App.Settings.PasteTopHotkeyVk);
        OverrideCtrlV = App.Settings.OverrideCtrlV;
    }

    private bool OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != Win32.WM_HOTKEY) return false;
        var id = wParam.ToInt32();
        if (id == IdShowPanel)
        {
            s_lastForegroundWindow = Win32.GetForegroundWindow();
            _dispatcher.TryEnqueue(() => ShowPanelRequested?.Invoke());
            return true;
        }
        if (id == IdPasteTop)
        {
            s_lastForegroundWindow = Win32.GetForegroundWindow();
            _dispatcher.TryEnqueue(() => PasteTopRequested?.Invoke());
            return true;
        }
        return false;
    }

    private void InstallHook()
    {
        _hook = Win32.SetWindowsHookExW(Win32.WH_KEYBOARD_LL, _hookProc, Win32.GetModuleHandleW(null), 0);
    }

    private IntPtr LowLevelHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

        var msg = (uint)wParam;
        var kb = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);

        // HookTestMode：注入的按键也按物理键处理（仅用于诊断，由 flag 文件开启）
        var injected = (kb.flags & Win32.LLKHF_INJECTED) != 0 && !HookTestMode;
        if (kb.vkCode != Win32.VK_V || injected)
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

        // 抬键：物理状态必须无条件复位，不能放在 SuppressHookAction 门控内。
        // 粘贴流程在按下后的一个消息泵回合内就置起 SuppressHookAction，并保持约 340ms；
        // 真人松手只需约 100ms，若抬键被门控跳过，_vKeyDown 会永久停留在 true，
        // 下一次 Ctrl+V 撞上自动重复判定被吞掉——表现为每隔一次 Ctrl+V 完全失效。
        if (msg == Win32.WM_KEYUP || msg == Win32.WM_SYSKEYUP)
        {
            var wasSwallowed = _vKeyDown;
            _vKeyDown = false;
            return wasSwallowed && (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0
                ? new IntPtr(1) // 按下已被吞掉，抬键一并吞掉，避免目标应用收到半截按键
                : Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (msg != Win32.WM_KEYDOWN && msg != Win32.WM_SYSKEYDOWN)
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
        if (SuppressHookAction || !OverrideCtrlV)
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
        if ((Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) == 0 || IsModifierDown())
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam); // 普通 v 键或 Shift/Alt+V：放行

        // 只在 Ctrl+V 首次按下时触发一次，忽略按住时的自动重复
        if (_vKeyDown) return new IntPtr(1);

        _vKeyDown = true;
        s_lastForegroundWindow = Win32.GetForegroundWindow();
        _dispatcher.TryEnqueue(() => PasteTopRequested?.Invoke());
        return new IntPtr(1); // 吞掉系统 Ctrl+V
    }

    private static bool IsModifierDown()
    {
        return (Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) != 0 ||
               (Win32.GetAsyncKeyState(Win32.VK_MENU) & 0x8000) != 0;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_hook);
        Win32.UnregisterHotKey(_hwnd, IdShowPanel);
        Win32.UnregisterHotKey(_hwnd, IdPasteTop);
    }
}
