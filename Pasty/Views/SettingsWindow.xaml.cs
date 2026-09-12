using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pasty.Models;
using Pasty.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace Pasty.Views;

public sealed partial class SettingsWindow : Window
{
    private bool _loading = true;
    private readonly IntPtr _hwnd;
    private Button? _recordingHotkeyButton;

    public SettingsWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = Localization.Get("SettingsTitle");
        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon(App.IconPath);
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(Scaled(640, 780));
        WindowThemeService.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);
        RootGrid.ActualThemeChanged += (s, e) =>
            WindowThemeService.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);
        AboutText.Text = Localization.Format("SettingsAbout", App.Version);

        _loading = true;
        SelectByTag(RetentionCombo, App.Settings.RetentionDays);
        SelectByTag(MaxItemsCombo, App.Settings.MaxItems);

        RefreshHotkeyButtons();

        OverrideToggle.IsOn = App.Settings.OverrideCtrlV;
        StartupToggle.IsOn = StartupService.IsEnabled();
        HideOnDeactivateToggle.IsOn = App.Settings.HideOnDeactivate;
        SelectByTag(ThemeCombo, App.Settings.Theme);
        SelectByTag(LanguageCombo, Localization.CurrentLanguage);
        _loading = false;

        UpdateHotkeyWarning();
        App.Hotkeys.RegistrationChanged += UpdateHotkeyWarning;
        UpdateClearButton();
        App.ViewModel.GroupsChanged += UpdateClearButton;
        // 设置窗口每次打开都是新实例，不退订的话旧实例会一直挂在事件上，
        // 下次注册失败（或下次列表变动）时对着已关闭窗口的控件写字
        Closed += (s, e) =>
        {
            // 录入时全局热键被临时注销；直接关窗口也必须恢复。
            if (_recordingHotkeyButton != null)
            {
                _recordingHotkeyButton = null;
                App.Hotkeys.ReRegister();
            }
            App.Hotkeys.RegistrationChanged -= UpdateHotkeyWarning;
            App.ViewModel.GroupsChanged -= UpdateClearButton;
        };
    }

    /// <summary>
    /// 把热键注册失败如实显示出来。这里是用户唯一会来换快捷键组合的地方，
    /// 提示放在别处等于没有。
    /// </summary>
    private void UpdateHotkeyWarning()
    {
        var errors = App.Hotkeys.RegistrationErrors;
        HotkeyWarning.Message = string.Join("\n", errors);
        HotkeyWarning.IsOpen = errors.Count > 0;
    }

    /// <summary>历史为空时“清空全部历史”没有任何事可做，置灰比点了没反应清楚。</summary>
    private void UpdateClearButton() => ClearButton.IsEnabled = App.ViewModel.TotalCount > 0;

    /// <summary>
    /// 把逻辑尺寸换算成当前显示器的物理像素。AppWindow.Resize 收的是物理像素，
    /// 以前直接传 640×780，在 150% 缩放下窗口只有 427×520 逻辑像素，
    /// 右侧的下拉框和开关会被挤出可见区域。
    /// </summary>
    private SizeInt32 Scaled(int width, int height)
    {
        var scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        return new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    public void ApplyTheme(ElementTheme theme) => RootGrid.RequestedTheme = theme;

    private static void SelectByTag(ComboBox combo, object value)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (Equals(item.Tag?.ToString(), value.ToString()))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private void Save() => App.Settings.Save();

    private void Retention_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        App.Settings.RetentionDays = int.Parse(((ComboBoxItem)RetentionCombo.SelectedItem).Tag.ToString()!);
        Save();
        RetentionService.Clean();
    }

    private void MaxItems_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        App.Settings.MaxItems = int.Parse(((ComboBoxItem)MaxItemsCombo.SelectedItem).Tag.ToString()!);
        Save();
    }

    private void RefreshHotkeyButtons()
    {
        ShowHotkeyButton.Content = AppSettings.HotkeyName(
            App.Settings.ShowHotkeyModifiers, App.Settings.ShowHotkeyVk);
        PasteHotkeyButton.Content = AppSettings.HotkeyName(
            App.Settings.PasteTopHotkeyModifiers, App.Settings.PasteTopHotkeyVk);
        ResetHotkeysButton.IsEnabled =
            App.Settings.ShowHotkeyModifiers != AppSettings.DefaultShowHotkeyModifiers ||
            App.Settings.ShowHotkeyVk != AppSettings.DefaultShowHotkeyVk ||
            App.Settings.PasteTopHotkeyModifiers != AppSettings.DefaultPasteHotkeyModifiers ||
            App.Settings.PasteTopHotkeyVk != AppSettings.DefaultPasteHotkeyVk;
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (_recordingHotkeyButton == null) App.Hotkeys.SuspendRegistration();
        _recordingHotkeyButton = button;
        button.Content = Localization.Get("PressHotkey");
        HotkeyCaptureInfo.Severity = InfoBarSeverity.Informational;
        HotkeyCaptureInfo.Message = Localization.Get("HotkeyCaptureHint");
        HotkeyCaptureInfo.IsOpen = true;
        button.Focus(FocusState.Keyboard);
    }

    private void HotkeyButton_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _recordingHotkeyButton)) return;
        e.Handled = true;
        var vk = (uint)e.Key;
        if (vk == 0x1B) // Esc 只负责取消录入，不能设成全局快捷键
        {
            EndHotkeyCapture();
            return;
        }
        if (IsModifierKey(vk)) return;

        var modifiers = CurrentModifiers();
        // 单字母或仅 Shift 会劫持所有应用里的正常输入，因此至少要求一个系统级修饰键。
        if ((modifiers & (AppSettings.MOD_CONTROL | AppSettings.MOD_ALT | AppSettings.MOD_WIN)) == 0)
        {
            HotkeyCaptureInfo.Severity = InfoBarSeverity.Warning;
            HotkeyCaptureInfo.Message = Localization.Get("HotkeyModifierRequired");
            return;
        }

        var editingShowHotkey = ReferenceEquals(_recordingHotkeyButton, ShowHotkeyButton);
        var otherModifiers = editingShowHotkey
            ? App.Settings.PasteTopHotkeyModifiers
            : App.Settings.ShowHotkeyModifiers;
        var otherVk = editingShowHotkey
            ? App.Settings.PasteTopHotkeyVk
            : App.Settings.ShowHotkeyVk;
        if (modifiers == otherModifiers && vk == otherVk)
        {
            HotkeyCaptureInfo.Severity = InfoBarSeverity.Warning;
            HotkeyCaptureInfo.Message = Localization.Get("HotkeyDuplicate");
            return;
        }

        if (editingShowHotkey)
        {
            App.Settings.ShowHotkeyModifiers = modifiers;
            App.Settings.ShowHotkeyVk = vk;
        }
        else
        {
            App.Settings.PasteTopHotkeyModifiers = modifiers;
            App.Settings.PasteTopHotkeyVk = vk;
        }
        Save();
        EndHotkeyCapture();
    }

    private void HotkeyButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _recordingHotkeyButton)) EndHotkeyCapture();
    }

    private void EndHotkeyCapture()
    {
        var wasRecording = _recordingHotkeyButton != null;
        _recordingHotkeyButton = null;
        HotkeyCaptureInfo.IsOpen = false;
        RefreshHotkeyButtons();
        if (wasRecording) App.Hotkeys.ReRegister();
    }

    private static bool IsModifierKey(uint vk)
        => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or
            0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static uint CurrentModifiers()
    {
        uint modifiers = 0;
        if ((Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0) modifiers |= AppSettings.MOD_CONTROL;
        if ((Win32.GetAsyncKeyState(Win32.VK_MENU) & 0x8000) != 0) modifiers |= AppSettings.MOD_ALT;
        if ((Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) != 0) modifiers |= AppSettings.MOD_SHIFT;
        if ((Win32.GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
            (Win32.GetAsyncKeyState(0x5C) & 0x8000) != 0) modifiers |= AppSettings.MOD_WIN;
        return modifiers;
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        EndHotkeyCapture();
        App.Settings.ShowHotkeyModifiers = AppSettings.DefaultShowHotkeyModifiers;
        App.Settings.ShowHotkeyVk = AppSettings.DefaultShowHotkeyVk;
        App.Settings.PasteTopHotkeyModifiers = AppSettings.DefaultPasteHotkeyModifiers;
        App.Settings.PasteTopHotkeyVk = AppSettings.DefaultPasteHotkeyVk;
        Save();
        App.Hotkeys.ReRegister();
        RefreshHotkeyButtons();
    }

    private void Override_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.OverrideCtrlV = OverrideToggle.IsOn;
        Save();
        App.Hotkeys.ReRegister();
    }

    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.AutoStart = StartupToggle.IsOn;
        Save();
        StartupService.SetEnabled(StartupToggle.IsOn);
    }

    private void HideOnDeactivate_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.HideOnDeactivate = HideOnDeactivateToggle.IsOn;
        Save();
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Theme = int.Parse(((ComboBoxItem)ThemeCombo.SelectedItem).Tag.ToString()!);
        Save();
        App.ApplyTheme();
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedItem is not ComboBoxItem item) return;
        var language = Localization.Normalize(item.Tag?.ToString());
        App.Settings.Language = language;
        Save();
        LanguageRestartInfo.Message = Localization.Get("RestartLanguage");
        LanguageRestartInfo.IsOpen = language != Localization.CurrentLanguage;
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var total = App.ViewModel.TotalCount;
        var pinned = App.ViewModel.PinnedCount;
        if (total == 0) return;

        var detail = pinned > 0
            ? Localization.Format("ClearDetailPinned", total, pinned)
            : Localization.Format("ClearDetail", total);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            // 对话框挂在 XamlRoot 的弹出层上，不在 RootGrid 之下，
            // 不显式带上主题就会用应用主题渲染——手动切到深色时弹出一个白框
            RequestedTheme = RootGrid.RequestedTheme,
            Title = Localization.Get("ClearTitle"),
            Content = detail,
            PrimaryButtonText = Localization.Get("Clear"),
            CloseButtonText = Localization.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            App.ViewModel.ClearAll();
    }
}
