using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DshManager.ViewModels;

namespace DshManager;

/// <summary>
/// 主窗口：无边框 + 自定义标题栏 + 壁纸背景层 + Thumb 四边/四角缩放。
/// 壁纸设置由 SettingsViewModel 驱动，变更即时刷新背景。
/// </summary>
public partial class MainWindow : Window
{
    private SettingsViewModel? _settingsVm;

    /// <summary>自定义最大化状态（透明窗口不使用系统 Maximized，避免覆盖任务栏）</summary>
    private bool _isMaximized;

    /// <summary>最大化前的窗口位置与尺寸（还原时使用）</summary>
    private Rect _normalBounds;

    public MainWindow()
    {
        InitializeComponent();
        UpdateMaximizeGlyph();
    }

    /// <summary>
    /// 绑定设置视图模型：订阅壁纸相关属性变更，使主窗口背景即时刷新。
    /// </summary>
    public void BindSettings(SettingsViewModel settingsVm)
    {
        _settingsVm = settingsVm;
        _settingsVm.PropertyChanged += OnSettingsPropertyChanged;
        ApplyWallpaper();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.WallpaperPath)
            or nameof(SettingsViewModel.IsWallpaperEnabled)
            or nameof(SettingsViewModel.WallpaperBlur)
            or nameof(SettingsViewModel.WallpaperOpacity))
        {
            ApplyWallpaper();
        }
    }

    // ==================== 标题栏：拖动窗口 / 双击最大化 ====================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点击标题栏上的按钮（最小化/最大化/关闭）时不触发窗口拖动
        if (IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        // 双击标题栏：切换最大化 / 还原
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        // 拖动窗口；最大化状态下不响应拖动（双击即可还原）
        if (_isMaximized)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 极少数鼠标状态异常时忽略，避免拖拽中断崩溃
        }
    }

    /// <summary>判断原始事件源是否位于某个按钮内（避免按钮点击冒泡触发窗口拖动）。</summary>
    private static bool IsInsideButton(DependencyObject? obj)
    {
        while (obj is not null)
        {
            if (obj is ButtonBase)
            {
                return true;
            }

            obj = obj is Visual ? VisualTreeHelper.GetParent(obj) : null;
        }

        return false;
    }

    // ==================== 标题栏窗口控制按钮 ====================

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    /// <summary>
    /// 窗口关闭前把防抖中尚未落盘的设置立即写出。
    /// 覆盖标题栏关闭按钮、Alt+F4、系统关机等所有关闭路径，避免丢失最后一次拖动调整（透明度/模糊度）。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        _settingsVm?.FlushPending();
        base.OnClosing(e);
    }

    /// <summary>
    /// 自定义最大化 / 还原：手动将窗口铺满工作区并记录还原位置，
    /// 规避 AllowsTransparency 下系统最大化覆盖任务栏的问题。
    /// </summary>
    private void ToggleMaximize()
    {
        if (_isMaximized)
        {
            Left = _normalBounds.Left;
            Top = _normalBounds.Top;
            Width = _normalBounds.Width;
            Height = _normalBounds.Height;
            _isMaximized = false;
        }
        else
        {
            _normalBounds = new Rect(Left, Top, Width, Height);

            var workArea = SystemParameters.WorkArea;
            Left = workArea.Left;
            Top = workArea.Top;
            Width = workArea.Width;
            Height = workArea.Height;
            _isMaximized = true;
        }

        UpdateMaximizeGlyph();
    }

    /// <summary>切换标题栏最大化 / 还原图标。</summary>
    private void UpdateMaximizeGlyph()
    {
        MaxIcon.Visibility = _isMaximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = _isMaximized ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================== 四边 / 四角拖拽缩放 ====================

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // 最大化状态下禁用缩放
        if (_isMaximized)
        {
            return;
        }

        var thumb = (Thumb)sender;
        double dx = e.HorizontalChange;
        double dy = e.VerticalChange;

        // 各方向调整窗口位置与尺寸，同时受 MinWidth/MinHeight 约束
        switch ((string)thumb.Tag)
        {
            case "N":
                if (Height - dy < MinHeight) dy = Height - MinHeight;
                Top += dy;
                Height -= dy;
                break;
            case "S":
                Height = Math.Max(MinHeight, Height + dy);
                break;
            case "W":
                if (Width - dx < MinWidth) dx = Width - MinWidth;
                Left += dx;
                Width -= dx;
                break;
            case "E":
                Width = Math.Max(MinWidth, Width + dx);
                break;
            case "NW":
                if (Width - dx < MinWidth) dx = Width - MinWidth;
                if (Height - dy < MinHeight) dy = Height - MinHeight;
                Left += dx;
                Top += dy;
                Width -= dx;
                Height -= dy;
                break;
            case "NE":
                if (Height - dy < MinHeight) dy = Height - MinHeight;
                Top += dy;
                Height -= dy;
                Width = Math.Max(MinWidth, Width + dx);
                break;
            case "SW":
                if (Width - dx < MinWidth) dx = Width - MinWidth;
                Left += dx;
                Width -= dx;
                Height = Math.Max(MinHeight, Height + dy);
                break;
            case "SE":
                Width = Math.Max(MinWidth, Width + dx);
                Height = Math.Max(MinHeight, Height + dy);
                break;
        }
    }

    // ==================== 壁纸背景层刷新 ====================

    /// <summary>根据设置刷新背景层：启用且可加载时显示壁纸，否则回退深海渐变。</summary>
    private void ApplyWallpaper()
    {
        if (_settingsVm is null)
        {
            return;
        }

        var resolved = _settingsVm.ResolveWallpaperPath(_settingsVm.WallpaperPath);
        var useWallpaper = _settingsVm.IsWallpaperEnabled && resolved is not null;

        if (!useWallpaper)
        {
            HideWallpaper();
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(resolved!, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            WallpaperImage.Source = bitmap;
            WallpaperImage.Opacity = _settingsVm.WallpaperOpacity;
            WallpaperBlurEffect.Radius = _settingsVm.WallpaperBlur;
            WallpaperImage.Visibility = Visibility.Visible;
            GradientFallback.Visibility = Visibility.Collapsed;
            DarkOverlay.Visibility = Visibility.Visible;
        }
        catch
        {
            // 图片损坏或不可解码时回退到渐变背景
            HideWallpaper();
        }
    }

    /// <summary>隐藏壁纸，恢复深海渐变背景。</summary>
    private void HideWallpaper()
    {
        WallpaperImage.Source = null;
        WallpaperImage.Visibility = Visibility.Collapsed;
        GradientFallback.Visibility = Visibility.Visible;
        DarkOverlay.Visibility = Visibility.Collapsed;
    }
}
