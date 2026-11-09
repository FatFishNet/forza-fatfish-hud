using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ForzaOverlay.App.Services;
using ForzaOverlay.Core.Configuration;

namespace ForzaOverlay.App.Ui;

public partial class HudLayoutWindow : Window
{
    private readonly AppSettings _settings;
    private readonly int _aspectIndex;
    private readonly HudLayoutProfile _profile;
    private readonly List<ModuleBox> _boxes = new();

    public HudLayoutWindow(AppSettings settings, int aspectIndex)
    {
        InitializeComponent();

        _settings = settings;
        _aspectIndex = Math.Clamp(aspectIndex, 0, HudLayoutDefaults.AspectNames.Length - 1);
        _profile = AspectProfile(settings, _aspectIndex);

        Loaded += OnWindowLoaded;
        StageHost.SizeChanged += OnStageHostSizeChanged;
        PreviewKeyDown += OnWindowPreviewKeyDown;
    }

    private sealed record ModuleBox(FrameworkElement Box, HudModuleDescriptor Module, HudModuleLayout Layout);

    private static HudLayoutProfile AspectProfile(AppSettings settings, int index)
    {
        return index switch
        {
            1 => settings.Profile16By10,
            2 => settings.Profile21By9,
            3 => settings.Profile32By9,
            4 => settings.Profile48By9,
            5 => settings.ProfileCustom,
            _ => settings.Profile16By9,
        };
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        PlaceOnTargetMonitor();
        WindowState = WindowState.Maximized;

        BuildBoxes();
        UpdateStage();
    }

    private void BuildBoxes()
    {
        foreach (HudModuleDescriptor module in HudModuleCatalog.Modules)
        {
            HudModuleLayout layout = Clone(module.Read(_profile));

            Grid box = new();

            box.Children.Add(new Border
            {
                Background = ToBrush(module.FillColor),
                BorderBrush = ToBrush(module.AccentColor),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
            });

            Thumb moveThumb = new() { Background = Brushes.Transparent };
            moveThumb.DragDelta += (_, args) => MoveBox(box, layout, args.HorizontalChange, args.VerticalChange);
            box.Children.Add(moveThumb);

            Thumb resizeThumb = new()
            {
                Width = 16,
                Height = 16,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = ToBrush(module.AccentColor),
            };
            resizeThumb.DragDelta += (_, args) => ResizeBox(box, layout, module.BaseWidth, module.BaseHeight, args.HorizontalChange);
            box.Children.Add(resizeThumb);

            Border label = new()
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB2, 0x00, 0x00, 0x00)),
                BorderBrush = ToBrush(module.AccentColor),
                BorderThickness = new Thickness(0, 0, 0, 2),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 8, 0, 0),
                IsHitTestVisible = false,
            };

            label.Child = new TextBlock
            {
                Text = module.Name,
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
            };

            box.Children.Add(label);

            box.Visibility = module.IsVisible(_settings) ? Visibility.Visible : Visibility.Collapsed;

            _boxes.Add(new ModuleBox(box, module, layout));
            StageCanvas.Children.Add(box);
        }
    }

    private static Brush ToBrush(string colorText)
    {
        return (Brush)new BrushConverter().ConvertFromString(colorText)!;
    }

    private void PlaceOnTargetMonitor()
    {
        IntPtr self = new WindowInteropHelper(this).Handle;
        if (self == IntPtr.Zero)
        {
            return;
        }

        IntPtr reference = AppServices.GameWindowHandle;
        if (reference == IntPtr.Zero && Owner is not null)
        {
            reference = new WindowInteropHelper(Owner).Handle;
        }

        IntPtr monitor = MonitorFromWindow(reference, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        MONITORINFO info = new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        RECT work = info.rcWork;
        SetWindowPos(self, IntPtr.Zero, work.Left, work.Top,
            work.Right - work.Left, work.Bottom - work.Top,
            SwpNoZOrder | SwpNoActivate);

        RECT bounds = info.rcMonitor;
        MonitorInfoText.Text =
            $"显示器 {bounds.Right - bounds.Left} × {bounds.Bottom - bounds.Top} · 比例 {HudLayoutDefaults.AspectNames[_aspectIndex]}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private void OnStageHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateStage();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SaveAndClose();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void UpdateStage()
    {
        double availableWidth = StageHost.ActualWidth;
        double availableHeight = StageHost.ActualHeight;
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        double aspect = HudLayoutDefaults.AspectRatio(_aspectIndex, _settings.CustomAspectWidth, _settings.CustomAspectHeight);
        double width = Math.Min(availableWidth, availableHeight * aspect);
        double height = width / aspect;

        Stage.Width = width;
        Stage.Height = height;
        StageCanvas.Width = width;
        StageCanvas.Height = height;

        PlaceBoxes();
    }

    private void PlaceBoxes()
    {
        foreach (ModuleBox item in _boxes)
        {
            PlaceBox(item.Box, item.Layout, item.Module.BaseWidth, item.Module.BaseHeight);
        }
    }

    private void PlaceBox(FrameworkElement box, HudModuleLayout layout, double baseWidth, double baseHeight)
    {
        double canvasWidth = StageCanvas.Width;
        double canvasHeight = StageCanvas.Height;
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            return;
        }

        double ratio = Math.Clamp(
            layout.WidthRatio > 0 ? layout.WidthRatio : 0.1,
            HudLayoutDefaults.MinWidthRatio,
            HudLayoutDefaults.MaxWidthRatio);

        box.Width = ratio * canvasWidth;
        box.Height = box.Width * baseHeight / baseWidth;

        double left = Math.Max(0, Math.Min(canvasWidth - box.Width, layout.X * canvasWidth));
        double top = Math.Max(0, Math.Min(canvasHeight - box.Height, layout.Y * canvasHeight));

        Canvas.SetLeft(box, left);
        Canvas.SetTop(box, top);

        layout.X = left / canvasWidth;
        layout.Y = top / canvasHeight;
        layout.WidthRatio = box.Width / canvasWidth;
    }

    private void MoveBox(FrameworkElement box, HudModuleLayout layout, double deltaX, double deltaY)
    {
        double canvasWidth = StageCanvas.Width;
        double canvasHeight = StageCanvas.Height;
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            return;
        }

        double left = Math.Max(0, Math.Min(canvasWidth - box.Width, Canvas.GetLeft(box) + deltaX));
        double top = Math.Max(0, Math.Min(canvasHeight - box.Height, Canvas.GetTop(box) + deltaY));

        Canvas.SetLeft(box, left);
        Canvas.SetTop(box, top);

        layout.X = left / canvasWidth;
        layout.Y = top / canvasHeight;
    }

    private void ResizeBox(FrameworkElement box, HudModuleLayout layout, double baseWidth, double baseHeight, double deltaX)
    {
        double canvasWidth = StageCanvas.Width;
        double canvasHeight = StageCanvas.Height;
        if (canvasWidth <= 0 || canvasHeight <= 0 || baseWidth <= 0)
        {
            return;
        }

        double width = Math.Clamp(
            box.Width + deltaX,
            HudLayoutDefaults.MinWidthRatio * canvasWidth,
            canvasWidth);

        box.Width = width;
        box.Height = width * baseHeight / baseWidth;

        double left = Math.Max(0, Math.Min(canvasWidth - box.Width, Canvas.GetLeft(box)));
        double top = Math.Max(0, Math.Min(canvasHeight - box.Height, Canvas.GetTop(box)));

        Canvas.SetLeft(box, left);
        Canvas.SetTop(box, top);

        layout.WidthRatio = box.Width / canvasWidth;
        layout.X = left / canvasWidth;
        layout.Y = top / canvasHeight;
    }

    private void SaveAndClose()
    {
        foreach (ModuleBox item in _boxes)
        {
            item.Module.Write(_profile, item.Layout);
        }

        _settings.HudAspectRatio = _aspectIndex;

        SettingsStore.Save(_settings);
        AppServices.ApplySettings(_settings);
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveAndClose();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static HudModuleLayout Clone(HudModuleLayout source)
    {
        return new HudModuleLayout
        {
            X = source.X,
            Y = source.Y,
            WidthRatio = source.WidthRatio,
        };
    }
}
