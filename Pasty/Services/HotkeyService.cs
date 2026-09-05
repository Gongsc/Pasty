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

    public static IntPtr LastForegroundWindow { get; private set; }

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
            LastForegroundWindow = Win32.GetForegroundWindow();
            _dispatcher.TryEnqueue(() => ShowPanelRequested?.Invoke());
            return true;
        }
        if (id == IdPasteTop)
        {
            LastForegroundWindow = Win32.GetForegroundWindow();
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
        if (nCode >= 0 && !SuppressHookAction && OverrideCtrlV)
        {
            var msg = (uint)wParam;
            var kb = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);

            // 跟踪 V 键物理状态：只在 Ctrl+V 首次按下时触发一次，忽略按住时的自动重复
            // HookTestMode：注入的按键也按物理键处理（仅用于诊断，由 flag 文件开启）
            var injected = (kb.flags & Win32.LLKHF_INJECTED) != 0 && !HookTestMode;
            if (kb.vkCode == Win32.VK_V && !injected)
            {
                if (msg == Win32.WM_KEYDOWN || msg == Win32.WM_SYSKEYDOWN)
                {
                    if ((Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) == 0 || IsModifierDown())
                        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam); // 普通 v 键或 Shift/Alt+V：放行
                    if (_vKeyDown)
                    {
                        Trace.Log("hook V-down 重复，吞掉");
                        return new IntPtr(1); // 自动重复：吞掉但不触发
                    }
                    _vKeyDown = true;
                    LastForegroundWindow = Win32.GetForegroundWindow();
                    Trace.Log($"hook 拦截 Ctrl+V，前台=0x{LastForegroundWindow.ToInt64():X}，焦点={Win32.GetFocusInfo()}，入队");
                    var queued = _dispatcher.TryEnqueue(() => PasteTopRequested?.Invoke());
                    Trace.Log($"hook 入队结果={queued}");
                    return new IntPtr(1); // 吞掉系统 Ctrl+V
                }
                if (msg == Win32.WM_KEYUP || msg == Win32.WM_SYSKEYUP)
                {
                    var wasDown = _vKeyDown;
                    _vKeyDown = false;
                    return wasDown && (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0
                        ? new IntPtr(1)
                        : Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
                }
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
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
