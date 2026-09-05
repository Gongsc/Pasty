namespace Pasty.Services;

using System.Runtime.InteropServices;

/// <summary>
/// UI 线程上的 Win32 消息窗口：承载剪贴板监听、热键 WM_HOTKEY 与托盘回调消息，
/// 消息由 XAML 消息循环泵出，回调天然位于 UI 线程。
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private const string ClassName = "Pasty_MsgWindow";
    private static bool s_classRegistered;
    private static MessageWindow? s_instance;

    public event Func<uint, IntPtr, IntPtr, bool>? ProcessMessage; // 返回 true 表示已处理

    public IntPtr Handle { get; }

    public MessageWindow()
    {
        if (!s_classRegistered)
        {
            var wc = new WNDCLASSW
            {
                lpfnWndProc = WndProc,
                hInstance = Win32.GetModuleHandleW(null),
                lpszClassName = ClassName,
            };
            Win32.RegisterClassW(ref wc);
            s_classRegistered = true;
        }
        Handle = Win32.CreateWindowExW(0, ClassName, "Pasty", 0, 0, 0, 0, 0,
            new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
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
