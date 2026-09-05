using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pasty.Models;
using Pasty.Services;

namespace Pasty.Views;

public sealed partial class SettingsWindow : Window
{
    // (显示名, modifiers, vk)
    private static readonly (string Name, uint Mods, uint Vk)[] ShowHotkeys =
    {
        ("Ctrl + Shift + V", AppSettings.MOD_CONTROL | AppSettings.MOD_SHIFT, AppSettings.VK_V),
        ("Ctrl + Shift + Space", AppSettings.MOD_CONTROL | AppSettings.MOD_SHIFT, 0x20),
        ("Win + Shift + V", AppSettings.MOD_WIN | AppSettings.MOD_SHIFT, AppSettings.VK_V),
        ("Alt + X", AppSettings.MOD_ALT, 0x58),
    };

    private static readonly (string Name, uint Mods, uint Vk)[] PasteHotkeys =
    {
        ("Ctrl + Alt + V", AppSettings.MOD_CONTROL | AppSettings.MOD_ALT, AppSettings.VK_V),
        ("Alt + V", AppSettings.MOD_ALT, AppSettings.VK_V),
        ("Ctrl + Alt + C", AppSettings.MOD_CONTROL | AppSettings.MOD_ALT, 0x43),
        ("Ctrl + Shift + X", AppSettings.MOD_CONTROL | AppSettings.MOD_SHIFT, 0x58),
    };

    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = "Pasty — 设置";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 780));

        _loading = true;
        SelectByTag(RetentionCombo, App.Settings.RetentionDays);
        SelectByTag(MaxItemsCombo, App.Settings.MaxItems);

        foreach (var h in ShowHotkeys) ShowHotkeyCombo.Items.Add(h.Name);
        foreach (var h in PasteHotkeys) PasteHotkeyCombo.Items.Add(h.Name);
        ShowHotkeyCombo.SelectedIndex = IndexOfHotkey(ShowHotkeys, App.Settings.ShowHotkeyModifiers, App.Settings.ShowHotkeyVk);
        PasteHotkeyCombo.SelectedIndex = IndexOfHotkey(PasteHotkeys, App.Settings.PasteTopHotkeyModifiers, App.Settings.PasteTopHotkeyVk);

        OverrideToggle.IsOn = App.Settings.OverrideCtrlV;
        StartupToggle.IsOn = StartupService.IsEnabled();
        HideOnDeactivateToggle.IsOn = App.Settings.HideOnDeactivate;
        SelectByTag(ThemeCombo, App.Settings.Theme);
        _loading = false;

        UpdateHotkeyWarning();
        App.Hotkeys.RegistrationChanged += UpdateHotkeyWarning;
        // 设置窗口每次打开都是新实例，不退订的话旧实例会一直挂在事件上，
        // 下次注册失败时对着已关闭窗口的控件写字
        Closed += (s, e) => App.Hotkeys.RegistrationChanged -= UpdateHotkeyWarning;
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

    private static int IndexOfHotkey((string Name, uint Mods, uint Vk)[] list, uint mods, uint vk)
    {
        for (var i = 0; i < list.Length; i++)
            if (list[i].Mods == mods && list[i].Vk == vk) return i;
        return 0;
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

    private void ShowHotkey_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var h = ShowHotkeys[ShowHotkeyCombo.SelectedIndex];
        App.Settings.ShowHotkeyModifiers = h.Mods;
        App.Settings.ShowHotkeyVk = h.Vk;
        Save();
        App.Hotkeys.ReRegister();
    }

    private void PasteHotkey_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var h = PasteHotkeys[PasteHotkeyCombo.SelectedIndex];
        App.Settings.PasteTopHotkeyModifiers = h.Mods;
        App.Settings.PasteTopHotkeyVk = h.Vk;
        Save();
        App.Hotkeys.ReRegister();
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

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "清空全部历史？",
            Content = "所有未置顶与已置顶的剪贴板记录都将被删除，且无法恢复。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            App.ViewModel.ClearAll();
    }
}
