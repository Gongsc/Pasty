using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
    private const int WindowWidth = 1060;
    private const int WindowHeight = 680;

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

        SearchBox.KeyDown += SearchBox_KeyDown;
        // ListViewItem 会先处理 Enter 和方向键，普通 KeyDown 有时收不到；预览事件在它之前拦截。
        ItemsList.PreviewKeyDown += ItemsList_KeyDown;

        Activated += (s, e) =>
        {
            // “关闭”仍然是隐藏到托盘，后台剪贴板监听不能随主窗口失焦退出。
            if (e.WindowActivationState == WindowActivationState.Deactivated &&
                App.Settings.HideMainWindowOnDeactivate)
                AppWindow.Hide();
        };
        AppWindow.Closing += (s, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };
        UpdateThemeIcon();
        // 图标配色与置灰文字是代码里按当前窗口主题取的（见 KindBrush），模板又是 OneTime 绑定，
        // 所以主题真的翻了就得重建一次行集合让它们重新求值，否则图标会停在旧主题的颜色上。
        RootGrid.ActualThemeChanged += (s, e) =>
        {
            UpdateThemeIcon();
            WindowThemeService.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);
            s_highContrast = null; // 主题一变，高对比度开关也要重探
            UpdateGroups();
        };
        WindowThemeService.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);
        UpdateCaptionInset();
        // 预留宽度问系统要、不要假定是 138：最大化和系统按钮宽度的变化都会改这个值，
        // 而首次布局完成之前它还是 0。位置/尺寸一变就重算，否则设置按钮会被压在关闭按钮底下
        AppWindow.Changed += (s, e) =>
        {
            if (e.DidSizeChange || e.DidPositionChange) UpdateCaptionInset();
        };
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

    // ---------- 显示与隐藏 ----------

    /// <summary>把逻辑尺寸换算成当前显示器的物理像素。</summary>
    private SizeInt32 Scaled(int width, int height)
    {
        var scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        return new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    public void ShowMainWindow()
    {
        AppWindow.Show(activateWindow: true);
        Activate();
        EnsureSelection();
    }

    /// <summary>
    /// 全局快捷键从后台唤起完整主窗口。Activate 不一定能抢到前台，仍需走
    /// ForceForeground；并把焦点交给搜索框，保留方向键选择与 Enter 粘贴体验。
    /// </summary>
    public void ShowFromHotkey()
    {
        ShowMainWindow();
        Win32.ForceForeground(_hwnd);
        Activate();
        SelectItem(App.ViewModel.Recent.FirstOrDefault());
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    /// <summary>
    /// 被第二个实例唤起：确保窗口可见、尺寸正确，并真的抢到前台。
    /// 光靠 ShowMainWindow 里的 Activate 不够——发起唤起的进程正在退出、本进程又不是前台，
    /// 系统会拒给普通 SetForegroundWindow，必须走 ForceForeground 那套附线程输入的强切。
    /// </summary>
    public void WakeUp()
    {
        ShowMainWindow();
        Win32.ForceForeground(_hwnd, tickAlt: true);
    }

    public void ShowSettingsWindow() => (App.Current as App)!.OpenSettingsWindow();

    private void EnsureSelection()
    {
        if (ItemsList.SelectedItem is ClipItem) return;

        SelectItem(_rows.OfType<ClipItem>().FirstOrDefault());
    }

    private void SelectItem(ClipItem? item)
        => ItemsList.SelectedIndex = item == null ? -1 : _rows.IndexOf(item);

    // ---------- 行内类型标记的可见性与配色 ----------

    /// <summary>
    /// 见 ClipItemTemplate 里的注释：只有“内容还在、且能直接 Ctrl+V 粘进大多数应用”的条目
    /// （文字 / 链接 / 图片）才有左边那根主色竖条。文件条目与已失效条目都没有。
    /// </summary>
    public static Visibility DirectMarkVisibility(PasteReadiness readiness)
        => readiness == PasteReadiness.Direct ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>内容正常的类型图标（按内容类型上色）。见 <see cref="DirectMarkVisibility"/>。</summary>
    public static Visibility LiveChipVisibility(PasteReadiness readiness)
        => readiness == PasteReadiness.Unavailable ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>内容已经没了：换成灰底的警告三角。见 <see cref="DirectMarkVisibility"/>。</summary>
    public static Visibility DeadChipVisibility(PasteReadiness readiness)
        => readiness == PasteReadiness.Unavailable ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>标题的两份写法：正常行走主题默认色，已失效的行走置灰色。见 <see cref="DirectMarkVisibility"/>。</summary>
    public static Visibility LiveTextVisibility(PasteReadiness readiness)
        => readiness == PasteReadiness.Unavailable ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>见 <see cref="LiveTextVisibility"/>。</summary>
    public static Visibility DeadTextVisibility(PasteReadiness readiness)
        => readiness == PasteReadiness.Unavailable ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>文字可以编辑，图片可以查看；文件没有对应的行内内容操作。</summary>
    public static Visibility RowActionVisibility(ClipType type)
        => type is ClipType.Text or ClipType.Image ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 按内容类型取图标画笔。颜色表在 ContentKindInfo.Color，亮/深各一档，
    /// 取哪一档看的是**本窗口**的 RootGrid.ActualTheme。不能走 Application.Current.Resources
    /// 那类“应用”级解析：本应用只设窗口级的 RequestedTheme，应用字典解析的始终是系统主题，
    /// 手动切亮/深色时颜色不会跟着变——那正是以前这个标记的 bug。
    /// 模板是 OneTime 绑定，所以主题真的翻了要靠 UpdateGroups() 重建一次行集合重新求值。
    ///
    /// 注意这里**绝不能返回 null**：Foreground 被显式设成 null 不是“用默认色”，而是“没有画刷”，
    /// 字就直接不画了（上一版就踩在这个上面：列表只剩图标，看不出历史还在）。
    /// 高对比度下取系统前景色，取不到就退回当前主题的调色板，总之必须是个真画笔。
    /// </summary>
    public static SolidColorBrush KindBrush(ContentKind kind)
    {
        var dark = Current?.RootGrid?.ActualTheme == ElementTheme.Dark;
        if (HighContrastOn)
        {
            var hc = HighContrastTextBrush();
            if (hc != null) return hc;
        }
        return CachedBrush(kind.Color(dark));
    }

    /// <summary>高对比度主题下的前景色（图标要跟着系统走，自己配色会把系统保证的对比度打掉）。</summary>
    private static SolidColorBrush? HighContrastTextBrush()
    {
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings()
                .GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground);
            return CachedBrush(Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B));
        }
        catch { return null; } // 取不到就退回调色板，不能退成 null
    }

    /// <summary>
    /// 系统是否开着高对比度主题。WinUI 3 的 ElementTheme 只有 Light/Dark/Default，从元素上问不出来，
    /// 只能去问 AccessibilitySettings。取不到（比如这个 WinRT 类在某些环境不可用）就当没开：
    /// 图标配色跟着系统走，判错只会让颜色不太对，不会丢任何信息（文字一律走主题色，见模板里的注释）。
    /// 值在主题事件上重探一次，不要永久缓存。
    /// </summary>
    private static bool HighContrastOn
    {
        get
        {
            if (s_highContrast == null)
            {
                try { s_highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
                catch { s_highContrast = false; }
            }
            return s_highContrast.Value;
        }
    }

    private static bool? s_highContrast;

    /// <summary>画笔按颜色复用：一次重建要为几百行取色，不能每行 new 一个。</summary>
    private static SolidColorBrush? CachedBrush(Windows.UI.Color color)
    {
        if (!s_brushCache.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            s_brushCache[color] = brush;
        }
        return brush;
    }

    private static readonly Dictionary<Windows.UI.Color, SolidColorBrush> s_brushCache = new();

    // ---------- 列表 ----------

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
            _rows.Add(new Models.HeaderRow { Title = Localization.Get("PinnedHeader") });
            foreach (var i in App.ViewModel.Pinned) _rows.Add(i);
        }
        // 未置顶记录按最后复制/使用日期分组。今天叫“最近”、昨天单独显示，
        // 更早的直接写完整日期，避免只写“更早”后还得逐行辨认时间。
        var today = DateTime.Today;
        foreach (var group in App.ViewModel.Recent
                     .GroupBy(i => i.LastUsedAt.Date)
                     .OrderByDescending(g => g.Key))
        {
            var title = group.Key == today
                ? Localization.Get("RecentHeader")
                : group.Key == today.AddDays(-1)
                    ? Localization.Get("YesterdayHeader")
                    : group.Key.ToString("D", System.Globalization.CultureInfo.CurrentUICulture);
            _rows.Add(new Models.HeaderRow { Title = title });
            foreach (var item in group) _rows.Add(item);
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
    /// 混成一句会让人以为历史被清空了。搜索匹配文字条目的内容与文件条目的文件名。
    /// </summary>
    private void UpdateListEmptyHint()
    {
        if (_rows.Count > 0)
        {
            ListEmptyHint.Visibility = Visibility.Collapsed;
            return;
        }
        ListEmptyHint.Text = string.IsNullOrWhiteSpace(App.ViewModel.Filter)
            ? Localization.Get("EmptyHistory")
            : Localization.Format("NoMatches", App.ViewModel.Filter);
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
    /// 把预览区从编辑态整体切回只读态。标题、两组按钮和编辑框必须一起复位：
    /// 只清 _editingItem 和 EditHost 会留下“编辑内容 + 保存/取消”的空壳界面，
    /// 而此时 _editingItem 已为 null，点“保存”走到 EndEdit 会直接返回，按钮形同失效。
    /// </summary>
    private void ExitEditMode()
    {
        _editingItem = null;
        EditHost.Visibility = Visibility.Collapsed;
        PreviewTitle.Text = Localization.Get("PreviewTitle");
        PreviewActions.Visibility = Visibility.Visible;
        EditActions.Visibility = Visibility.Collapsed;
    }

    private void UpdatePreview(ClipItem item)
    {
        ExitEditMode();
        TextPreviewHost.Visibility = Visibility.Collapsed;
        ImagePreviewHost.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        EmptyHint.Text = EmptyHintDefault;

        PreviewSource.Text = item.SourceText;
        PreviewType.Text = item.KindLabel;
        PreviewSize.Text = item.SizeDetailText;
        PreviewDate.Text = item.DateDetailText;
        // 内容已经不在了就写不进剪贴板，粘/复制按下去只会静默失败，不如直接禁用
        CopyButton.IsEnabled = item.CanWriteToClipboard;
        PasteButton.IsEnabled = item.CanWriteToClipboard;
        ToolTipService.SetToolTip(PasteButton, item.CanWriteToClipboard ? null : item.ReadinessHint);
        ToolTipService.SetToolTip(CopyButton, item.CanWriteToClipboard ? null : item.ReadinessHint);

        switch (item.Type)
        {
            case ClipType.Text:
                TextPreviewHost.Visibility = Visibility.Visible;
                PreviewTextBlock.Text = item.Text;
                EditPreviewButton.Visibility = Visibility.Visible;
                break;

            case ClipType.Image when item.ImagePath != null && File.Exists(item.ImagePath):
                ImagePreviewHost.Visibility = Visibility.Visible;
                PreviewImage.Source = new BitmapImage(new Uri(item.ImagePath!));
                EditPreviewButton.Visibility = Visibility.Collapsed;
                break;

            case ClipType.File:
                // 文件条目没有“内容”可看，展示路径清单（FullPreviewText 会把已经不在了的那几个标出来）
                TextPreviewHost.Visibility = Visibility.Visible;
                PreviewTextBlock.Text = item.FullPreviewText;
                EditPreviewButton.Visibility = Visibility.Collapsed;
                break;

            default:
                // 图片文件被清掉了：只剩一个空图片框会让人以为在加载，直接说一句人话
                PreviewImage.Source = null;
                EditPreviewButton.Visibility = Visibility.Collapsed;
                EmptyHint.Text = item.ReadinessHint;
                EmptyHint.Visibility = Visibility.Visible;
                break;
        }
    }

    /// <summary>与 MainWindow.xaml 里 EmptyHint 的默认文案保持一致。</summary>
    private static string EmptyHintDefault => Localization.Get("EmptyPreview");

    private void ShowEmptyPreview()
    {
        ExitEditMode();
        TextPreviewHost.Visibility = Visibility.Collapsed;
        ImagePreviewHost.Visibility = Visibility.Collapsed;
        EmptyHint.Text = EmptyHintDefault;
        EmptyHint.Visibility = Visibility.Visible;
        PreviewSource.Text = string.Empty;
        PreviewType.Text = string.Empty;
        PreviewSize.Text = string.Empty;
        PreviewDate.Text = string.Empty;
        // 没有选中条目时两个按钮都没对象可粘，跟着置灰
        CopyButton.IsEnabled = false;
        PasteButton.IsEnabled = false;
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
        // 唤起窗口的热键有“触发那一刻”可记，而鼠标双击与“粘贴”按钮没有，
        // 以前只能退回 GetForegroundWindow()——那是 Pasty 自己，校验不通过就只写剪贴板不发按键，
        // 用户看到的就是“条目跳到了最顶端，目标应用里什么都没出现”
        var target = ForegroundService.ResolvePasteTarget();

        await PasteService.PasteAsync(item, target);
        App.ViewModel.Touch(item);
    }

    // ---------- 编辑（预览区内就地编辑） ----------

    private ClipItem? _editingItem;

    private void RowAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ClipItem item) return;
        SelectItem(item);

        if (item.Type == ClipType.Text)
            StartEdit(item);
        else if (item.Type == ClipType.Image)
            UpdatePreview(item);
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
        PreviewTitle.Text = Localization.Get("EditTitle");
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
            PasteSelected();
        }
        else if (e.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)
        {
            e.Handled = true;
            // 搜索框继续持有焦点：第一次方向键就直接移动选择，也不会让列表项画出白色键盘焦点框。
            MoveSelection(e.Key == Windows.System.VirtualKey.Up ? -1 : 1);
        }
    }

    private void ItemsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            PasteSelected();
        }
        else if (e.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)
        {
            e.Handled = true;
            MoveSelection(e.Key == Windows.System.VirtualKey.Up ? -1 : 1);
            // 即使用户先用鼠标或 Tab 把焦点放进列表，移动后也交还给搜索框，
            // 让选中项始终只显示统一的深色高亮，而不是额外叠一圈白色焦点框。
            SearchBox.Focus(FocusState.Keyboard);
        }
    }

    /// <summary>
    /// 方向键只在真实条目间移动，跳过夹在 _rows 里的分组标题。
    /// 否则选中标题后按 Enter 没有 ClipItem 可粘，看起来就像 Enter 突然失效。
    /// </summary>
    private void MoveSelection(int direction)
    {
        var selectedIndex = ItemsList.SelectedIndex;
        if (selectedIndex < 0)
            selectedIndex = direction > 0 ? -1 : _rows.Count;

        for (var index = selectedIndex + direction;
             index >= 0 && index < _rows.Count;
             index += direction)
        {
            if (_rows[index] is not ClipItem) continue;
            ItemsList.SelectedIndex = index;
            ItemsList.ScrollIntoView(_rows[index]);
            return;
        }

        if (direction < 0)
            SearchBox.Focus(FocusState.Keyboard);
    }

    private async void PasteSelected()
    {
        if (ItemsList.SelectedItem is ClipItem item)
            await PasteItemAsync(item);
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
