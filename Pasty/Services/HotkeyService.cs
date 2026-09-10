using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Pasty.Models;

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
    private int _hookError;
    private readonly Win32.KeyboardHookDelegate _hookProc; // 防 GC
    private bool _vKeyDown; // 被钩子吞掉的物理 V 是否仍处于按下状态（用于忽略自动重复并配对吞掉抬键）
    public static volatile bool OverrideCtrlV;

    /// <summary>
    /// 热键注册与钩子安装的失败说明，空表示一切正常。
    /// RegisterHotKey 失败是常态而非异常——组合键被别的常驻程序抢先注册就会失败。
    /// 原先四个返回值全部丢弃，用户只知道“按了没反应”，无从判断是撞了快捷键、
    /// 还是这个功能坏了，换一个组合就能解决的问题会一直卡在那里。
    /// </summary>
    public IReadOnlyList<string> RegistrationErrors { get; private set; } = Array.Empty<string>();

    /// <summary>RegistrationErrors 更新后触发，供设置界面刷新提示。</summary>
    public event Action? RegistrationChanged;

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

        var errors = new List<string>();
        TryRegister(IdShowPanel, App.Settings.ShowHotkeyModifiers, App.Settings.ShowHotkeyVk, "唤出面板", errors);
        TryRegister(IdPasteTop, App.Settings.PasteTopHotkeyModifiers, App.Settings.PasteTopHotkeyVk, "粘贴第一条", errors);

        OverrideCtrlV = App.Settings.OverrideCtrlV;
        // 钩子只在启动时装一次；开着“覆盖 Ctrl+V”却没装上钩子，这个开关就是纯粹的摆设
        if (OverrideCtrlV && _hook == IntPtr.Zero)
            errors.Add($"键盘钩子安装失败（错误码 {_hookError}），“覆盖系统 Ctrl+V”不会生效，请重启 Pasty");

        RegistrationErrors = errors;
        RegistrationChanged?.Invoke();
    }

    /// <summary>注册一个热键；失败时把可操作的说明追加到 errors。</summary>
    private void TryRegister(int id, uint mods, uint vk, string label, List<string> errors)
    {
        if (Win32.RegisterHotKey(_hwnd, id, mods, vk)) return;
        var err = Marshal.GetLastWin32Error();
        errors.Add($"{label}快捷键 {AppSettings.HotkeyName(mods, vk)} 注册失败（错误码 {err}），" +
                   "通常是已被其他程序占用，换一个组合即可");
        Trace.Log($"RegisterHotKey 失败 id={id} err={err}");
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
        _hookError = _hook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        if (_hook == IntPtr.Zero) Trace.Log($"SetWindowsHookEx 失败 err={_hookError}");
    }

    private IntPtr LowLevelHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

        var msg = (uint)wParam;
        var kb = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);

        if (kb.dwExtraInfo == Win32.PastyInjectedInput)
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

        var injected = (kb.flags & Win32.LLKHF_INJECTED) != 0;
        // HookTestMode：注入按键也进入下面的状态判断，便于诊断；正常运行时直接放行给目标应用。
        if (kb.vkCode != Win32.VK_V || (injected && !HookTestMode))
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

        // 抬键无条件复位并与被吞掉的按下事件配对，目标应用不能收到半截 V 事件。
        if (msg == Win32.WM_KEYUP || msg == Win32.WM_SYSKEYUP)
        {
            var wasSwallowed = _vKeyDown;
            _vKeyDown = false;
            return wasSwallowed
                ? new IntPtr(1) // 按下已被吞掉，抬键一并吞掉，避免目标应用收到半截按键
                : Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (msg != Win32.WM_KEYDOWN && msg != Win32.WM_SYSKEYDOWN)
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
        if (!OverrideCtrlV)
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
