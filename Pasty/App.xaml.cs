using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Pasty.Models;
using Pasty.Services;
using Pasty.Views;

namespace Pasty;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static MessageWindow MessageWindow { get; private set; } = null!;
    public static ClipboardMonitor ClipboardMonitor { get; private set; } = null!;
    public static HotkeyService Hotkeys { get; private set; } = null!;
    public static MainViewModel ViewModel { get; private set; } = null!;

    /// <summary>
    /// 窗口图标（标题栏左上角、Alt+Tab、任务栏）。unpackaged 应用没有包清单提供图标，
    /// 每个窗口都得自己 AppWindow.SetIcon，否则显示的是那个通用的空白窗口图标。
    /// </summary>
    public static string IconPath { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Pasty.ico");

    /// <summary>
    /// 版本号。唯一的真相是 csproj 里的 <c>&lt;Version&gt;</c>，设置页、CI 产物名与 Release 标题都从它派生，
    /// 不在界面上手写数字——否则改了 csproj 这里忘了改，版本号就成了摆设。
    /// </summary>
    public static string Version { get; } =
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private RetentionService _retention = null!;
    private SingleInstanceService _singleInstance = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += (s, e) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pasty", "error.log"),
                    $"[{DateTime.Now:HH:mm:ss}] {e.Exception}\r\n");
            }
            catch { }
            e.Handled = true;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 单实例守卫排在所有初始化之前：第二个实例不该读索引、不装钩子，
        // 更不该在退出时把一份空快照落盘、覆盖掉真正在跑的那个实例的数据
        if (!SingleInstanceService.TryBecomeFirstInstance())
        {
            Trace.Log("已有实例在运行：唤起它并退出本进程");
            SingleInstanceService.WakeRunningInstance();
            Trace.Flush(300); // 日志走后台队列，不手动等一下就整条丢失
            Current.Exit();
            return;
        }

        Settings = AppSettings.Load();
        StorageService.Load();
        ViewModel = new MainViewModel();

        MessageWindow = new MessageWindow();
        ClipboardMonitor = new ClipboardMonitor(MessageWindow);
        Hotkeys = new HotkeyService(MessageWindow);
        _singleInstance = new SingleInstanceService(MessageWindow);
        _singleInstance.WakeRequested += () => MainWindow.Current?.WakeUp();
        _retention = new RetentionService(DispatcherQueue.GetForCurrentThread());

        var tray = new TrayIconService(MessageWindow);
        tray.OpenRequested += () => MainWindow.Current?.ShowAsWindow();
        tray.SettingsRequested += OpenSettingsWindow;
        tray.ExitRequested += Exit;

        ClipboardMonitor.ClipboardChanged += async item =>
        {
            await ViewModel.AddOrUpdateAsync(item);
        };

        Hotkeys.PasteTopRequested += () => _ = PasteFirstAsync();
        Hotkeys.ShowPanelRequested += () => MainWindow.Current?.ShowAsPanel();

        // 前台窗口跟踪要在任何粘贴发生之前起来：双击条目、点“粘贴”按钮都靠它找目标
        ForegroundService.Start();

        await ViewModel.LoadFromStorageAsync();
        _retention.Start();

        _mainWindow = new MainWindow();
        _mainWindow.Activate();
        ApplyTheme();
    }

    public void OpenSettingsWindow()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (s, e) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
        ApplyTheme();
    }

    public static void ApplyTheme()
    {
        var theme = Settings.Theme switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        MainWindow.Current?.ApplyTheme(theme);
        (Current as App)?._settingsWindow?.ApplyTheme(theme);
    }

    public new static void Exit()
    {
        Hotkeys.Dispose();
        ForegroundService.Stop();
        TrayIconService.Remove();
        // 落盘必须在 Current.Exit() 之前且是同步的：Save() 走后台队列，
        // 进程一结束队列里的写入就没了，退出前的复制/置顶/编辑会全部丢失
        StorageService.Flush();
        Trace.Flush();
        Current.Exit();
    }

    public static async Task PasteFirstAsync()
    {
        var item = ViewModel.TopItem;
        if (item == null)
        {
            Trace.Log("paste-first 无可粘贴条目");
            return;
        }
        var target = ForegroundService.ResolvePasteTarget();
        MainWindow.Current?.HideIfPanel();
        // 只记录类型与长度，内容本身绝不落盘：用户复制的往往就是密码和令牌
        Trace.Log($"paste-first 条目={item.Type} 长度={(item.Type == ClipType.Text ? item.Text.Length : 0)}");
        await PasteService.PasteAsync(item, target);
        ViewModel.Touch(item);
    }
}
