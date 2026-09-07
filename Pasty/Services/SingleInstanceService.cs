namespace Pasty.Services;

/// <summary>
/// 单实例守卫。
///
/// 为什么必须有：两个 Pasty 进程会各自 RegisterHotKey（后启动的那个必然报"快捷键已被占用"，
/// 组合键已被前者抢走）、各装一个低级键盘钩子（同一次 Ctrl+V 被处理两遍）、
/// 各自监听剪贴板并往同一个 index.json 写。以前只能靠"运行前先杀掉上一个实例"。
///
/// 工作方式：用一个会话内命名的 Mutex 判定谁是第一个实例；第二个实例不读索引、不装钩子、
/// 不落盘，只向已运行实例的消息窗口投递一条自定义窗口消息让它把自己带到前台，然后立刻退出。
/// </summary>
public sealed class SingleInstanceService
{
    private const string MutexName = "Pasty_SingleInstance";
    private const string WakeMessageName = "Pasty_Wake";

    /// <summary>
    /// 互斥体句柄必须活到进程结束——一被 GC 回收（或显式释放）就等于把"唯一性"让出去，
    /// 之后启动的实例会以为自己是第一个，回到两个进程并存的老问题。
    /// </summary>
    private static Mutex? _mutex;
    private static uint _wakeMessage;

    /// <summary>
    /// 抢占"第一个实例"的位置，必须在任何初始化之前调用。
    /// 返回 false 表示已有实例在跑，调用方应当 <see cref="WakeRunningInstance"/> 后立刻退出。
    /// </summary>
    public static bool TryBecomeFirstInstance()
    {
        // 名字不加 Global\ 前缀，落在当前会话的 Local\ 命名空间：创建全局对象需要
        // SeCreateGlobalPrivilege，普通用户会被直接拒绝；而"同一个登录用户重复启动"
        // 正是这里要挡的场景，会话级作用域刚好够用
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        // 两个进程各自注册同一个字符串，拿到的消息号才相同。WM_APP+n 那种自编号在
        // 跨进程时各说各话，绝不能用来做实例间通信
        _wakeMessage = Win32.RegisterWindowMessageW(WakeMessageName);
        return createdNew;
    }

    /// <summary>第二个实例调用：叫已运行的实例把主窗口带到前台。</summary>
    public static void WakeRunningInstance()
    {
        var target = Win32.FindWindowW(MessageWindow.ClassName, null);
        if (target == IntPtr.Zero || _wakeMessage == 0)
        {
            Trace.Log($"唤醒失败：找不到已运行实例（hwnd=0x{target.ToInt64():X} msg={_wakeMessage}）");
            return;
        }
        Win32.PostMessageW(target, _wakeMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>收到唤醒消息（在 UI 线程上，由 XAML 消息泵驱动）。</summary>
    public event Action? WakeRequested;

    public SingleInstanceService(MessageWindow messageWindow)
    {
        messageWindow.ProcessMessage += OnMessage;
    }

    private bool OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != _wakeMessage) return false;
        WakeRequested?.Invoke();
        return true;
    }
}
