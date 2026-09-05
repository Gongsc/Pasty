using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Pasty.Models;
using Pasty.Services;
using WinRT.Interop;

namespace Pasty.Views;

public class HeaderRow
{
    public string Title { get; set; } = string.Empty;
}

public class RowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? ItemTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => item is Models.HeaderRow ? HeaderTemplate : ItemTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}

public sealed partial class MainWindow : Window
{
    public static new MainWindow? Current { get; private set; }

    private bool _panelMode;
    private bool _suppressSelection;
    private readonly ObservableCollection<object> _rows = new();
    private ClipItem? _selectedItem;

    public MainWindow()
    {
        InitializeComponent();
        Current = this;

        Title = "Pasty";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1060, 680));

        _rows.CollectionChanged += (s, e) => { };
        ItemsList.ItemsSource = _rows;

        App.ViewModel.GroupsChanged += UpdateGroups;
        UpdateGroups();

        RootGrid.KeyDown += RootGrid_KeyDown;
        SearchBox.KeyDown += SearchBox_KeyDown;
        ItemsList.KeyDown += ItemsList_KeyDown;
        ItemsList.ContainerContentChanging += ItemsList_ContainerContentChanging;

        Activated += (s, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated &&
                _panelMode && App.Settings.HideOnDeactivate)
                HidePanel();
        };
        AppWindow.Closing += (s, e) =>
        {
            e.Cancel = true;
            HidePanel();
        };
        UpdateThemeIcon();
        RootGrid.ActualThemeChanged += (s, e) => UpdateThemeIcon();
        // 预览卡片高度自适应内容，但不超过可用空间（留出标题与状态行）
        PreviewArea.SizeChanged += (s, e) =>
            PreviewCard.MaxHeight = Math.Max(180, e.NewSize.Height - 48);
        if (ItemsList.Items.Count > 0) ItemsList.SelectedIndex = 0;
    }

    public void ApplyTheme(ElementTheme theme) => RootGrid.RequestedTheme = theme;

    private void UpdateThemeIcon()
    {
        var glyph = RootGrid.ActualTheme == ElementTheme.Dark ? "\uE706" : "\uE708"; // sun / moon
        ((FontIcon)ThemeButton.Content).Glyph = glyph;
    }

    // ---------- 显示形态 ----------

    public void ShowAsPanel()
    {
        _panelMode = true;
        Win32.GetCursorPos(out var p);
        var area = DisplayArea.GetFromPoint(new PointInt32(p.X, p.Y), DisplayAreaFallback.Nearest);
        var width = 440;
        var x = Math.Clamp(p.X - width / 2, area.WorkArea.X, area.WorkArea.X + area.WorkArea.Width - width);
        var y = Math.Clamp(p.Y + 12, area.WorkArea.Y, area.WorkArea.Y + area.WorkArea.Height - 520);
        AppWindow.Move(new PointInt32(x, y));
        AppWindow.Show(activateWindow: true);
        Activate();
        // WinUI 的 Activate() 不保证抢到前台，必须显式强制，否则面板收不到键盘输入
        Win32.ForceForeground(WindowNative.GetWindowHandle(this));
        Activate();
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    public void ShowAsWindow()
    {
        _panelMode = false;
        AppWindow.Show(activateWindow: true);
        Activate();
        EnsureSelection();
    }

    public void EnsureVisible() => ShowAsWindow();

    public void HidePanel()
    {
        AppWindow.Hide();
    }

    public void HideIfPanel()
    {
        if (_panelMode) HidePanel();
    }

    public void ShowSettingsWindow() => (App.Current as App)!.OpenSettingsWindow();

    private void EnsureSelection()
    {
        if (ItemsList.SelectedItem == null && ItemsList.Items.Count > 0)
            ItemsList.SelectedIndex = 0;
    }

    // ---------- 列表 ----------

    private void UpdateGroups()
    {
        // 保留当前选中项，全量重建行集合
        var selected = ItemsList.SelectedItem as ClipItem ?? _selectedItem;

        _rows.Clear();
        if (App.ViewModel.Pinned.Count > 0)
        {
            _rows.Add(new Models.HeaderRow { Title = "\u2606 已置顶" });
            foreach (var i in App.ViewModel.Pinned) _rows.Add(i);
        }
        _rows.Add(new Models.HeaderRow { Title = "最近" });
        foreach (var i in App.ViewModel.Recent) _rows.Add(i);

        _suppressSelection = true;
        var index = selected != null ? _rows.IndexOf(selected) : -1;
        if (index < 0)
        {
            var firstItem = _rows.OfType<ClipItem>().FirstOrDefault();
            index = firstItem != null ? _rows.IndexOf(firstItem) : -1;
        }
        ItemsList.SelectedIndex = index;
        _suppressSelection = false;

        // 程序化选中不会触发（或被抑制）SelectionChanged，这里手动刷新预览
        if (ItemsList.SelectedItem is ClipItem current)
        {
            _selectedItem = current;
            UpdatePreview(current);
        }
        else
        {
            ShowEmptyPreview();
        }
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (_editingItem != null && !ReferenceEquals(ItemsList.SelectedItem, _editingItem))
            EndEdit(save: false); // 切换条目时放弃未保存的编辑
        if (ItemsList.SelectedItem is ClipItem item)
        {
            _selectedItem = item;
            UpdatePreview(item);
        }
        else if (ItemsList.SelectedItem == null)
        {
            ShowEmptyPreview();
        }
        // 选中分组标题行时保持原预览不变
    }

    private void ItemsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is ClipItem item)
            _ = PasteItemAsync(item);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => App.ViewModel.SetFilter(SearchBox.Text);

    // ---------- 预览 ----------

    private void UpdatePreview(ClipItem item)
    {
        _editingItem = null;
        EditHost.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        PreviewMeta.Text = item.MetaText;
        if (item.Type == ClipType.Text)
        {
            TextPreviewHost.Visibility = Visibility.Visible;
            ImagePreviewHost.Visibility = Visibility.Collapsed;
            PreviewTextBlock.Text = item.Text;
            EditPreviewButton.Visibility = Visibility.Visible;
        }
        else
        {
            TextPreviewHost.Visibility = Visibility.Collapsed;
            ImagePreviewHost.Visibility = Visibility.Visible;
            PreviewImage.Source = item.ImagePath != null
                ? new BitmapImage(new Uri(item.ImagePath))
                : null;
            EditPreviewButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowEmptyPreview()
    {
        _editingItem = null;
        TextPreviewHost.Visibility = Visibility.Collapsed;
        ImagePreviewHost.Visibility = Visibility.Collapsed;
        EditHost.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Visible;
        PreviewMeta.Text = string.Empty;
    }

    private async void CopyPreview_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not ClipItem item) return;
        ClipboardMonitor.Suspended = true;
        try { await PasteService.WriteToClipboardAsync(item); }
        finally { ClipboardMonitor.Suspended = false; }
        App.ViewModel.Touch(item);
    }

    private async void PastePreview_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not ClipItem item) return;
        await PasteItemAsync(item);
    }

    private async Task PasteItemAsync(ClipItem item)
    {
        var target = HotkeyService.LastForegroundWindow != IntPtr.Zero
            ? HotkeyService.LastForegroundWindow
            : Win32.GetForegroundWindow();
        if (_panelMode) HidePanel();
        if (target != WindowNative.GetWindowHandle(this))
            await PasteService.PasteAsync(item, target);
        App.ViewModel.Touch(item);
    }

    // ---------- 编辑（预览面板内就地编辑） ----------

    private ClipItem? _editingItem;

    private void EditItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ClipItem item && item.Type == ClipType.Text)
            StartEdit(item);
    }

    private void EditPreview_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is ClipItem item && item.Type == ClipType.Text)
            StartEdit(item);
    }

    private void StartEdit(ClipItem item)
    {
        _editingItem = item;
        TextPreviewHost.Visibility = Visibility.Collapsed;
        ImagePreviewHost.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        EditHost.Visibility = Visibility.Visible;
        PreviewEditBox.Text = item.Text;
        PreviewTitle.Text = "编辑内容";
        PreviewActions.Visibility = Visibility.Collapsed;
        EditActions.Visibility = Visibility.Visible;
        PreviewEditBox.Focus(FocusState.Programmatic);
        PreviewEditBox.SelectionStart = PreviewEditBox.Text.Length;
    }

    private void EndEdit(bool save)
    {
        if (_editingItem == null) return;
        var item = _editingItem;
        _editingItem = null;

        if (save)
        {
            App.ViewModel.UpdateText(item, PreviewEditBox.Text);
        }

        PreviewTitle.Text = "全文预览";
        PreviewActions.Visibility = Visibility.Visible;
        EditActions.Visibility = Visibility.Collapsed;
        if (ItemsList.SelectedItem == item)
            UpdatePreview(item);
        else
            ShowEmptyPreview();
    }

    private void SaveEdit_Click(object sender, RoutedEventArgs e) => EndEdit(save: true);

    private void CancelEdit_Click(object sender, RoutedEventArgs e) => EndEdit(save: false);

    private void PinItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ClipItem item)
            App.ViewModel.TogglePin(item);
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ClipItem item)
            App.ViewModel.Remove(item);
    }

    // ---------- 键盘 ----------

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            PasteSelectedOrTop();
        }
        else if (e.Key == Windows.System.VirtualKey.Down && ItemsList.Items.Count > 0)
        {
            ItemsList.Focus(FocusState.Keyboard);
            if (ItemsList.SelectedIndex < 0) ItemsList.SelectedIndex = 0;
            e.Handled = true;
        }
    }

    private void ItemsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            PasteSelectedOrTop();
        }
        else if (e.Key == Windows.System.VirtualKey.Up && ItemsList.SelectedIndex <= 0)
        {
            SearchBox.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    private async void PasteSelectedOrTop()
    {
        var item = ItemsList.SelectedItem as ClipItem ?? App.ViewModel.TopItem;
        if (item != null)
            await PasteItemAsync(item);
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && _panelMode)
        {
            HidePanel();
            e.Handled = true;
        }
    }

    private void ItemsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ClipItem item) return;
        var grid = args.ItemContainer.ContentTemplateRoot as Grid;
        var border = grid?.FindName("TypeIcon") as Border;
        var text = grid?.FindName("TypeIconText") as TextBlock;
        if (border == null || text == null) return;
        if (item.Type == ClipType.Text)
        {
            text.Text = "T";
            border.Background = (Brush)Application.Current.Resources["SystemControlBackgroundAccentBrush"];
            text.Foreground = new SolidColorBrush(Colors.White);
        }
        else
        {
            text.Text = "\uE8B9"; // 图片
            border.Background = (Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumBrush"];
            text.Foreground = (Brush)Application.Current.Resources["SystemControlForegroundAccentBrush"];
        }
    }

    // ---------- 标题栏按钮 ----------

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Theme = RootGrid.ActualTheme == ElementTheme.Dark ? 1 : 2;
        App.Settings.Save();
        App.ApplyTheme();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();
}
