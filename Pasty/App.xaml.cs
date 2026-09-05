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

    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private RetentionService _retention = null!;

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
        Settings = AppSettings.Load();
        StorageService.Load();
        ViewModel = new MainViewModel();

        MessageWindow = new MessageWindow();
        ClipboardMonitor = new ClipboardMonitor(MessageWindow);
        Hotkeys = new HotkeyService(MessageWindow);
        _retention = new RetentionService(DispatcherQueue.GetForCurrentThread());

        var tray = new TrayIconService(MessageWindow);
        tray.OpenRequested += () => MainWindow.Current?.ShowAsWindow();
        tray.SettingsRequested += OpenSettingsWindow;
        tray.ExitRequested += Exit;

        ClipboardMonitor.ClipboardChanged += async item =>
        {
            await ViewModel.AddOrUpdateAsync(item);
        };

        Hotkeys.PasteTopRequested += () => _ = PasteFirstAsync(useLastForeground: true);
        Hotkeys.ShowPanelRequested += () => MainWindow.Current?.ShowAsPanel();

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
        TrayIconService.Remove();
        Current.Exit();
    }

    public static async Task PasteFirstAsync(bool useLastForeground)
    {
        var item = ViewModel.TopItem;
        if (item == null)
        {
            Trace.Log("paste-first 无可粘贴条目");
            return;
        }
        var target = useLastForeground ? HotkeyService.ConsumeLastForegroundWindow() : IntPtr.Zero;
        if (!Win32.IsPasteTarget(target)) target = Win32.GetForegroundWindow();
        MainWindow.Current?.HideIfPanel();
        // 只记录类型与长度，内容本身绝不落盘：用户复制的往往就是密码和令牌
        Trace.Log($"paste-first 条目={item.Type} 长度={(item.Type == ClipType.Text ? item.Text.Length : 0)}");
        await PasteService.PasteAsync(item, target);
        ViewModel.Touch(item);
    }
}
