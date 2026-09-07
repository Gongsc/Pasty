using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Pasty.Models;
using Pasty.Services;
using WinRT.Interop;

namespace Pasty.Views;

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

    // 逻辑像素；AppWindow 收的是物理像素，用时按 DPI 换算
    private const int PanelWidth = 440;
    private const int PanelHeight = 520;
    private const int WindowWidth = 1060;
    private const int WindowHeight = 680;

    private bool _panelMode;
    private bool _suppressSelection;
    private readonly ObservableCollection<object> _rows = new();
    private ClipItem? _selectedItem;
    private readonly IntPtr _hwnd;

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        _hwnd = WindowNative.GetWindowHandle(this);

        Title = "Pasty";
        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon(App.IconPath);
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(Scaled(WindowWidth, WindowHeight));

        _rows.CollectionChanged += (s, e) => { };
        ItemsList.ItemsSource = _rows;

        App.ViewModel.GroupsChanged += UpdateGroups;
        UpdateGroups();

        RootGrid.KeyDown += RootGrid_KeyDown;
        SearchBox.KeyDown += SearchBox_KeyDown;
        ItemsList.KeyDown += ItemsList_KeyDown;

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
        UpdateCaptionInset();
        // 预留宽度问系统要、不要假定是 138：最大化和系统按钮宽度的变化都会改这个值，
        // 而首次布局完成之前它还是 0。位置/尺寸一变就重算，否则设置按钮会被压在关闭按钮底下
        AppWindow.Changed += (s, e) =>
        {
            if (e.DidSizeChange || e.DidPositionChange) UpdateCaptionInset();
        };
        if (ItemsList.Items.Count > 0) ItemsList.SelectedIndex = 0;
    }

    public void ApplyTheme(ElementTheme theme) => RootGrid.RequestedTheme = theme;

    private void UpdateThemeIcon()
    {
        var glyph = RootGrid.ActualTheme == ElementTheme.Dark ? "\uE706" : "\uE708"; // sun / moon
        ((FontIcon)ThemeButton.Content).Glyph = glyph;
    }

    /// <summary>
    /// 标题栏右侧要给系统的最小化/最大化/关闭按钮让出宽度。ExtendsContentIntoTitleBar
    /// 之后那块区域仍由系统绘制并接收点击，自绘内容压上去就是一片点不动的按钮。
    /// RightInset 是物理像素，Margin 收的是逻辑像素，中间要除以 DPI 缩放；
    /// 首次布局完成前它是 0，这时退回 138（标准三枚按钮的逻辑宽度）。
    /// </summary>
    private void UpdateCaptionInset()
    {
        var scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        var inset = AppWindow.TitleBar.RightInset / scale;
        TitleBarActions.Margin = new Thickness(0, 0, inset > 0 ? inset : 138, 0);
    }

    /// <summary>
    /// 面板形态下把右侧预览整列折叠掉。列宽写死成 340 + *，在 440 宽的面板里
    /// 预览列只剩几十像素，标题和三个按钮会挤成一团；这时列表应该独占整个宽度。
    /// </summary>
    private void SetPreviewVisible(bool visible)
    {
        if (visible)
        {
            ListColumn.Width = new GridLength(340);
            PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
            PreviewColumn.Width = new GridLength(0);
        }
        // 分隔线所在的列是 Auto，线一折叠这一列自然就归零
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewDivider.Visibility = v;
        PreviewArea.Visibility = v;
    }

    // ---------- 显示形态 ----------

    /// <summary>把逻辑尺寸换算成当前显示器的物理像素。</summary>
    private SizeInt32 Scaled(int width, int height)
    {
        var scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        return new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    /// <summary>
    /// 把 desired 夹到 [origin, origin + extent - size] 内。
    /// 直接用 Math.Clamp 在窗口比工作区还大时会因 min &gt; max 抛 ArgumentException，
    /// 而这条路径由热键触发、异常被 App 的兜底处理器吞掉，表现就是面板压根不出现。
    /// </summary>
    private static int FitInto(int desired, int origin, int extent, int size)
        => extent <= size ? origin : Math.Clamp(desired, origin, origin + extent - size);

    public void ShowAsPanel()
    {
        _panelMode = true;
        SetPreviewVisible(false);
        Win32.GetCursorPos(out var p);
        var area = DisplayArea.GetFromPoint(new PointInt32(p.X, p.Y), DisplayAreaFallback.Nearest);

        // 必须真的缩到面板尺寸再定位。原先只算了 440×520 的坐标却没有 Resize，
        // 窗口仍是 1060×680，于是按面板宽度居中的结果是整个窗口大幅偏出光标右侧。
        var size = Scaled(PanelWidth, PanelHeight);
        AppWindow.Resize(size);

        var x = FitInto(p.X - size.Width / 2, area.WorkArea.X, area.WorkArea.Width, size.Width);
        var y = FitInto(p.Y + 12, area.WorkArea.Y, area.WorkArea.Height, size.Height);
        AppWindow.Move(new PointInt32(x, y));
        AppWindow.Show(activateWindow: true);
        Activate();
        // WinUI 的 Activate() 不保证抢到前台，必须显式强制，否则面板收不到键盘输入
        Win32.ForceForeground(_hwnd);
        Activate();
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    public void ShowAsWindow()
    {
        // 从面板形态切回来时必须恢复尺寸，否则托盘打开的主窗口会一直是 440 宽的面板
        if (_panelMode) AppWindow.Resize(Scaled(WindowWidth, WindowHeight));
        _panelMode = false;
        SetPreviewVisible(true);
        AppWindow.Show(activateWindow: true);
        Activate();
        EnsureSelection();
    }

    public void EnsureVisible() => ShowAsWindow();

    /// <summary>
    /// 被第二个实例唤起：确保窗口可见、尺寸正确，并真的抢到前台。
    /// 光靠 ShowAsWindow 里的 Activate 不够——发起唤起的进程正在退出、本进程又不是前台，
    /// 系统会拒给普通 SetForegroundWindow，必须走 ForceForeground 那套附线程输入的强切。
    /// </summary>
    public void WakeUp()
    {
        ShowAsWindow();
        Win32.ForceForeground(_hwnd, tickAlt: true);
    }

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

    /// <summary>
    /// 行内类型标记的显隐。两套底色以前是在 ContainerContentChanging 里
    /// 用 Application.Current.Resources 查出来直接赋给 Border 的，而那个字典解析的是
    /// “应用”主题，绕过了本窗口的 RootGrid.RequestedTheme——手动切主题时标记颜色不跟着变，
    /// 而且其中一处写死了 Colors.White，高对比度主题下对比度不受任何保证。
    /// 现在两个 Border 都写在 XAML 里走 ThemeResource，这里只决定露哪一个。
    /// </summary>
    public static Visibility TextChipVisibility(ClipType type)
        => type == ClipType.Text ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>见 <see cref="TextChipVisibility"/>。</summary>
    public static Visibility ImageChipVisibility(ClipType type)
        => type == ClipType.Text ? Visibility.Collapsed : Visibility.Visible;

    private void UpdateGroups()
    {
        // 保留当前选中项与正在进行的编辑，全量重建行集合
        var selected = ItemsList.SelectedItem as ClipItem ?? _selectedItem;
        var editing = _editingItem;
        var pendingText = editing != null ? PreviewEditBox.Text : null;
        var pendingCaret = editing != null ? PreviewEditBox.SelectionStart : 0;

        _rows.Clear();
        if (App.ViewModel.Pinned.Count > 0)
        {
            _rows.Add(new Models.HeaderRow { Title = "\u2606 已置顶" });
            foreach (var i in App.ViewModel.Pinned) _rows.Add(i);
        }
        // 「最近」这个标题以前是无条件加的：一条最近记录都没有时，
        // 列表里只剩一个孤零零的标题，看着像卡在加载中
        if (App.ViewModel.Recent.Count > 0)
        {
            _rows.Add(new Models.HeaderRow { Title = "最近" });
            foreach (var i in App.ViewModel.Recent) _rows.Add(i);
        }
        UpdateListEmptyHint();

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

        // 上面的刷新会把预览切回只读态。若重建前正在编辑且该条目仍在列表中，
        // 连同未保存的文本与光标位置一起恢复——否则用户打字时别处来一次复制
        // （或置顶/删除/保留期清理触发的重建）就会静默吞掉编辑内容。
        if (editing != null && pendingText != null && _rows.Contains(editing))
        {
            if (!ReferenceEquals(ItemsList.SelectedItem, editing))
            {
                _suppressSelection = true;
                ItemsList.SelectedIndex = _rows.IndexOf(editing);
                _suppressSelection = false;
                _selectedItem = editing;
                UpdatePreview(editing);
            }
            StartEdit(editing, pendingText, pendingCaret);
        }
    }

    /// <summary>
    /// 空列表提示。「一条历史都没有」和「搜索没命中」要用不同的文案：
    /// 混成一句会让人以为历史被清空了。搜索只匹配文字条目（见 MainViewModel.RebuildGroups），
    /// 所以没命中时顺带说明这一点。
    /// </summary>
    private void UpdateListEmptyHint()
    {
        if (_rows.Count > 0)
        {
            ListEmptyHint.Visibility = Visibility.Collapsed;
            return;
        }
        ListEmptyHint.Text = string.IsNullOrWhiteSpace(App.ViewModel.Filter)
            ? "还没有剪贴板历史。复制任意文字或图片，这里就会出现。"
            : $"没有匹配「{App.ViewModel.Filter}」的记录。搜索只查找文字条目。";
        ListEmptyHint.Visibility = Visibility.Visible;
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

    /// <summary>
    /// 把预览面板从编辑态整体切回只读态。标题、两组按钮和编辑框必须一起复位：
    /// 只清 _editingItem 和 EditHost 会留下“编辑内容 + 保存/取消”的空壳界面，
    /// 而此时 _editingItem 已为 null，点“保存”走到 EndEdit 会直接返回，按钮形同失效。
    /// </summary>
    private void ExitEditMode()
    {
        _editingItem = null;
        EditHost.Visibility = Visibility.Collapsed;
        PreviewTitle.Text = "全文预览";
        PreviewActions.Visibility = Visibility.Visible;
        EditActions.Visibility = Visibility.Collapsed;
    }

    private void UpdatePreview(ClipItem item)
    {
        ExitEditMode();
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
        ExitEditMode();
        TextPreviewHost.Visibility = Visibility.Collapsed;
        ImagePreviewHost.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Visible;
        PreviewMeta.Text = string.Empty;
    }

    private async void CopyPreview_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not ClipItem item) return;
        await PasteService.WriteToClipboardAsync(item); // 内部已登记自身写入，通知会被过滤
        App.ViewModel.Touch(item);
    }

    private async void PastePreview_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not ClipItem item) return;
        await PasteItemAsync(item);
    }

    private async Task PasteItemAsync(ClipItem item)
    {
        // 目标窗口的解析见 ForegroundService.ResolvePasteTarget：
        // 热键唤出的面板有“触发那一刻”可记，而鼠标双击与“粘贴”按钮没有，
        // 以前只能退回 GetForegroundWindow()——那是 Pasty 自己，校验不通过就只写剪贴板不发按键，
        // 用户看到的就是“条目跳到了最顶端，目标应用里什么都没出现”
        var target = ForegroundService.ResolvePasteTarget();

        if (_panelMode) HidePanel();
        await PasteService.PasteAsync(item, target);
        App.ViewModel.Touch(item);
    }

    // ---------- 编辑（预览面板内就地编辑） ----------

    private ClipItem? _editingItem;

    private void EditItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ClipItem item || item.Type != ClipType.Text) return;
        // 面板形态下预览整列是折叠的，编辑框在里面根本看不见，
        // 原先点了这个按钮就像什么都没发生；先切回窗口形态再进编辑态
        if (_panelMode) ShowAsWindow();
        StartEdit(item);
    }

    private void EditPreview_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is ClipItem item && item.Type == ClipType.Text)
            StartEdit(item);
    }

    /// <summary>进入编辑态。text/caret 用于列表重建后原样恢复未保存的编辑。</summary>
    private void StartEdit(ClipItem item, string? text = null, int caret = -1)
    {
        _editingItem = item;
        TextPreviewHost.Visibility = Visibility.Collapsed;
        ImagePreviewHost.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        EditHost.Visibility = Visibility.Visible;
        PreviewEditBox.Text = text ?? item.Text;
        PreviewTitle.Text = "编辑内容";
        PreviewActions.Visibility = Visibility.Collapsed;
        EditActions.Visibility = Visibility.Visible;
        PreviewEditBox.Focus(FocusState.Programmatic);
        PreviewEditBox.SelectionStart = caret >= 0
            ? Math.Min(caret, PreviewEditBox.Text.Length)
            : PreviewEditBox.Text.Length;
    }

    private void EndEdit(bool save)
    {
        if (_editingItem == null) return;
        var item = _editingItem;
        var text = PreviewEditBox.Text;
        _editingItem = null;

        // UpdateText 会触发列表重建；_editingItem 已清空，重建不会再把编辑态恢复回来
        if (save) App.ViewModel.UpdateText(item, text);

        // 只读态的界面复位统一由 UpdatePreview / ShowEmptyPreview 里的 ExitEditMode 负责
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

    // ---------- 标题栏按钮 ----------

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Theme = RootGrid.ActualTheme == ElementTheme.Dark ? 1 : 2;
        App.Settings.Save();
        App.ApplyTheme();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();
}
