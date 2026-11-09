using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ForzaOverlay.App.Services;
using ForzaOverlay.Core.Configuration;
using ForzaOverlay.Core.Logging;

namespace ForzaOverlay.App.Ui;

public partial class SettingsWindow : Window
{
    private const double FixedWidthPixels = 1160;
    private const double FixedHeightPixels = 900;
    private const string DefaultIp = "127.0.0.1";
    private const int DefaultPort = 23333;
    private const double PerfSettingsBarWidth = 284;

    private static readonly Brush SelectedNavBackground = new SolidColorBrush(Color.FromRgb(0xEB, 0xEE, 0xF2));
    private static readonly Brush SelectedNavForeground = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x18));
    private static readonly Brush NormalNavForeground = new SolidColorBrush(Color.FromRgb(0x5A, 0x5F, 0x68));
    private static readonly Brush OkHintBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x9E, 0x5B));
    private static readonly Brush ErrorHintBrush = new SolidColorBrush(Color.FromRgb(0xF9, 0xAB, 0x00));

    private bool _ipHintIsError;
    private bool _portHintIsError;

    private readonly DispatcherTimer _logTimer;
    private readonly DispatcherTimer _perfTimer;
    private readonly DispatcherTimer _hotkeyHintTimer;
    private readonly DispatcherTimer _endpointHintTimer;
    private readonly DispatcherTimer _refreshRateHintTimer;
    private readonly DispatcherTimer _aboutCopyHintTimer;
    private static UpdateInfo? _pendingUpdate;
    private static bool _updateChecked;
    private readonly DispatcherTimer _styleHintTimer;
    private readonly DispatcherTimer _aspectHintTimer;
    private AppSettings _settings;
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();

        _settings = SettingsStore.Load();

        IpBox.Text = _settings.TelemetryIp;
        PortBox.Text = FormatPort(_settings.TelemetryPort);

        AspectCombo.ItemsSource = HudLayoutDefaults.AspectNames;
        AspectCombo.SelectedIndex = Math.Clamp(_settings.HudAspectRatio, 0, HudLayoutDefaults.AspectNames.Length - 1);

        string[] hotkeyOptions = BuildHotkeyOptions();
        HotkeyCombo.ItemsSource = hotkeyOptions;
        HotkeyCombo.SelectedIndex = Array.IndexOf(hotkeyOptions, _settings.HotkeyText);

        string[] styleOptions = ["经典", "GHOST LADDER", "SHIFT TREE", "仿NFS19"];
        InstrumentStyleCombo.ItemsSource = styleOptions;
        InstrumentStyleCombo.SelectedIndex = _settings.InstrumentStyle switch
        {
            HudInstrumentStyle.Festival => 1,
            HudInstrumentStyle.ShiftTree => 2,
            HudInstrumentStyle.Nfs19 => 3,
            _ => 0,
        };

        PerformanceSwitch.IsChecked = _settings.ShowPerformancePanel;
        RedlineCalibrationSwitch.IsChecked = _settings.EnableRedlineCalibration;
        ShiftHintCombo.ItemsSource = new[] { "关闭", "4灯提示" };
        ShiftHintCombo.SelectedIndex = _settings.ShiftHintStyle == HudShiftHintStyle.Rgb3 ? 1 : 0;
        UpdateShiftHintAvailability();
        UpdateInstrumentStyleRowVisibility();
        AppServices.PrivilegeStateChanged += OnPrivilegeStateChanged;
        UpdatePrivilegeBanner();
        TireSwitch.IsChecked = _settings.ShowTirePanel;
        PerfSwitch.IsChecked = _settings.ShowPerfPanel;
        PerfLogSwitch.IsChecked = _settings.PerfLogEnabled;

        OverspeedSoundSwitch.IsChecked = _settings.EnableOverspeedSound;
        OverspeedSpeedBox.Text = _settings.OverspeedSpeedKmh.ToString(CultureInfo.InvariantCulture);
        OverspeedCountCombo.ItemsSource = new[] { "3 声", "5 声", "10 声", "一直响" };
        OverspeedCountCombo.SelectedIndex = _settings.OverspeedSoundCount switch
        {
            3 => 0,
            10 => 2,
            0 => 3,
            _ => 1,
        };
        OverspeedPathBox.Text = _settings.OverspeedSoundPath;
        OverspeedDelayCombo.ItemsSource = new[] { "100 ms", "200 ms", "300 ms", "400 ms", "500 ms" };
        OverspeedDelayCombo.SelectedIndex = Math.Clamp((_settings.OverspeedSoundDelayMs - 100) / 100, 0, 4);
        OverspeedVolumeSlider.Value = Math.Clamp(_settings.OverspeedSoundVolume, 0, 100);
        OverspeedVolumeText.Text = Math.Clamp(_settings.OverspeedSoundVolume, 0, 100).ToString(CultureInfo.InvariantCulture) + "%";
        UpdateOverspeedOptionsVisibility();
        AlwaysShowSwitch.IsChecked = _settings.AlwaysShowHud;
        PanelOpacitySlider.Value = _settings.BasicPanelBackgroundOpacity;
        PanelOpacityText.Text = _settings.BasicPanelBackgroundOpacity.ToString(CultureInfo.InvariantCulture) + "%";

        string[] refreshOptions = ["60 FPS", "30 FPS", "以游戏发包为准"];
        RefreshRateCombo.ItemsSource = refreshOptions;
        RefreshRateCombo.SelectedIndex = _settings.HudRefreshRate switch
        {
            0 => 1,
            2 => 2,
            _ => 0,
        };

        CustomWidthBox.Text = _settings.CustomAspectWidth.ToString(CultureInfo.InvariantCulture);
        CustomHeightBox.Text = _settings.CustomAspectHeight.ToString(CultureInfo.InvariantCulture);
        UpdateCustomSizeVisibility();
        UpdateDerivedRatio();

        UpdateNavSelection(NavTelemetryButton);
        InitAboutPage();

        _logTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1.5),
        };
        _logTimer.Tick += OnLogTimerTick;
        _logTimer.Start();

        _perfTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _perfTimer.Tick += OnPerfTimerTick;
        _perfTimer.Start();

        _hotkeyHintTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _hotkeyHintTimer.Tick += OnHotkeyHintTimerTick;

        _endpointHintTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _endpointHintTimer.Tick += OnEndpointHintTimerTick;

        _refreshRateHintTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _refreshRateHintTimer.Tick += OnRefreshRateHintTimerTick;

        _aboutCopyHintTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _aboutCopyHintTimer.Tick += OnAboutCopyHintTimerTick;

        _styleHintTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _styleHintTimer.Tick += OnStyleHintTimerTick;

        _aspectHintTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _aspectHintTimer.Tick += OnAspectHintTimerTick;

        _loading = false;

        StartUpdateCheck();

        UpdatePerfReadout();
    }

    private static string[] BuildHotkeyOptions()
    {
        string[] options = new string[12];
        for (int i = 0; i < options.Length; i++)
        {
            options[i] = "F" + (i + 1).ToString(CultureInfo.InvariantCulture);
        }

        return options;
    }

    private void OnPerfTimerTick(object? sender, EventArgs e)
    {
        UpdatePerfReadout();

    }

    private void UpdatePerfReadout()
    {
        double fps = PerformanceMonitor.Fps;
        double cpu = PerformanceMonitor.CpuPercent;
        double mem = PerformanceMonitor.MemoryMb;
        double gpu = PerformanceMonitor.GpuPercent;
        double vram = PerformanceMonitor.VramMb;

        PerfFpsText.Text = fps.ToString("F0", CultureInfo.InvariantCulture);
        PerfCpuText.Text = cpu.ToString("F1", CultureInfo.InvariantCulture) + "%";
        PerfMemText.Text = mem.ToString("F0", CultureInfo.InvariantCulture) + "MB";
        PerfGpuText.Text = gpu.ToString("F1", CultureInfo.InvariantCulture) + "%";
        PerfVramText.Text = vram.ToString("F0", CultureInfo.InvariantCulture) + "MB";

        PerfFpsBar.Width = Math.Clamp(fps / 60.0, 0, 1) * PerfSettingsBarWidth;
        PerfCpuBar.Width = Math.Clamp(cpu / 100.0, 0, 1) * PerfSettingsBarWidth;
        PerfMemBar.Width = Math.Clamp(mem / 512.0, 0, 1) * PerfSettingsBarWidth;
        PerfGpuBar.Width = Math.Clamp(gpu / 100.0, 0, 1) * PerfSettingsBarWidth;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GwlStyle);
        _ = SetWindowLong(hwnd, GwlStyle, style | WsSysMenu | WsMinimizeBox);

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Width = FixedWidthPixels / dpi.DpiScaleX;
        Height = FixedHeightPixels / dpi.DpiScaleY;
    }

    private const int GwlStyle = -16;
    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    protected override void OnClosed(EventArgs e)
    {
        AppServices.PrivilegeStateChanged -= OnPrivilegeStateChanged;
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        string tag = button.Tag as string ?? string.Empty;

        PagePort.Visibility = tag == "port" ? Visibility.Visible : Visibility.Collapsed;
        PageTelemetry.Visibility = tag == "telemetry" ? Visibility.Visible : Visibility.Collapsed;
        PageHud.Visibility = tag == "hud" ? Visibility.Visible : Visibility.Collapsed;
        PageLog.Visibility = tag == "log" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        ContentScroll.ScrollToTop();

        if (tag == "log")
        {
            UpdateLogView();
        }

        UpdateNavSelection(button);
    }

    private void UpdateNavSelection(Button selected)
    {
        ApplyNavState(NavPortButton, ReferenceEquals(NavPortButton, selected));
        ApplyNavState(NavTelemetryButton, ReferenceEquals(NavTelemetryButton, selected));
        ApplyNavState(NavHudButton, ReferenceEquals(NavHudButton, selected));
        ApplyNavState(NavLogButton, ReferenceEquals(NavLogButton, selected));
        ApplyNavState(NavAboutButton, ReferenceEquals(NavAboutButton, selected));
    }

    private static void ApplyNavState(Button button, bool isSelected)
    {
        button.Background = isSelected ? SelectedNavBackground : Brushes.Transparent;
        button.Foreground = isSelected ? SelectedNavForeground : NormalNavForeground;
        button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void OnPrivilegeStateChanged()
    {
        Dispatcher.BeginInvoke(UpdatePrivilegeBanner);
    }

    private void UpdatePrivilegeBanner()
    {
        PrivilegeBanner.Visibility = AppServices.IsPrivilegeSufficient
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void RestartElevated_Click(object sender, RoutedEventArgs e)
    {
        App.RestartAsElevated();
    }
    private void PanelOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.BasicPanelBackgroundOpacity = (int)Math.Round(PanelOpacitySlider.Value);
        PanelOpacityText.Text = _settings.BasicPanelBackgroundOpacity.ToString(CultureInfo.InvariantCulture) + "%";
        Commit();
    }
    private void RefreshRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RefreshRateCombo.SelectedIndex < 0)
        {
            return;
        }

        _settings.HudRefreshRate = RefreshRateCombo.SelectedIndex switch
        {
            1 => 0,
            2 => 2,
            _ => 1,
        };

        Commit();
        ShowRefreshRateHint();
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, null);
    }
    private void UpdateInstrumentStyleRowVisibility()
    {
        bool visible = PerformanceSwitch.IsChecked == true;
        Visibility visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InstrumentStyleRow.Visibility = visibility;
        RedlineCalibrationRow.Visibility = visibility;
        ShiftHintRow.Visibility = visibility;
    }
    private void RedlineCalibrationSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.EnableRedlineCalibration = RedlineCalibrationSwitch.IsChecked == true;
        if (!_settings.EnableRedlineCalibration)
        {
            // 换挡提示依赖红区校正，关闭红区校正时一并关闭。
            _settings.ShiftHintStyle = HudShiftHintStyle.Off;
            ShiftHintCombo.SelectedIndex = 0;
        }

        UpdateShiftHintAvailability();
        ShowInlineHint(RedlineCalibrationHint);
        Commit();
    }

    private void ShiftHintCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ShiftHintCombo.SelectedIndex < 0)
        {
            return;
        }

        if (!_settings.EnableRedlineCalibration)
        {
            ShiftHintCombo.SelectedIndex = 0;
            return;
        }

        _settings.ShiftHintStyle = ShiftHintCombo.SelectedIndex == 1 ? HudShiftHintStyle.Rgb3 : HudShiftHintStyle.Off;
        ShowInlineHint(ShiftHintHint);
        Commit();
    }

    private void UpdateShiftHintAvailability()
    {
        bool enabled = _settings.EnableRedlineCalibration;
        ShiftHintCombo.IsEnabled = enabled;
        ShiftHintCombo.Opacity = enabled ? 1.0 : 0.45;
    }

    private void InstrumentStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || InstrumentStyleCombo.SelectedIndex < 0)
        {
            return;
        }

        _settings.InstrumentStyle = InstrumentStyleCombo.SelectedIndex switch
        {
            1 => HudInstrumentStyle.Festival,
            2 => HudInstrumentStyle.ShiftTree,
            3 => HudInstrumentStyle.Nfs19,
            _ => HudInstrumentStyle.Classic,
        };

        Commit();
        ShowStyleHint();

        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, null);
    }
    private void OverspeedSoundSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.EnableOverspeedSound = OverspeedSoundSwitch.IsChecked == true;
        UpdateOverspeedOptionsVisibility();
        ShowInlineHint(OverspeedSoundHint);
        Commit();
    }

    private void UpdateOverspeedOptionsVisibility()
    {
        OverspeedSoundOptions.Visibility = _settings.EnableOverspeedSound
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OverspeedCountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OverspeedCountCombo.SelectedIndex < 0)
        {
            return;
        }

        _settings.OverspeedSoundCount = OverspeedCountCombo.SelectedIndex switch
        {
            0 => 3,
            2 => 10,
            3 => 0,
            _ => 5,
        };
        ShowInlineHint(OverspeedCountHint);
        Commit();
    }

    private void OverspeedDelayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OverspeedDelayCombo.SelectedIndex < 0)
        {
            return;
        }

        _settings.OverspeedSoundDelayMs = 100 + (OverspeedDelayCombo.SelectedIndex * 100);
        ShowInlineHint(OverspeedDelayHint);
        Commit();
    }

    private void OverspeedVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OverspeedVolumeText is null)
        {
            return;
        }

        int volume = Math.Clamp((int)Math.Round(OverspeedVolumeSlider.Value), 0, 100);
        OverspeedVolumeText.Text = volume.ToString(CultureInfo.InvariantCulture) + "%";
        if (_loading)
        {
            return;
        }

        _settings.OverspeedSoundVolume = volume;
        Commit();
    }

    private void OverspeedPreview_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OverspeedPathBox.Text))
        {
            return;
        }

        OverspeedSoundPlayer.Shared?.Preview();
    }

    private void OverspeedStopPreview_Click(object sender, RoutedEventArgs e)
    {
        OverspeedSoundPlayer.Shared?.StopAll();
    }

    private void OverspeedSpeedBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (int.TryParse(OverspeedSpeedBox.Text.Trim(), out int speed) && speed > 0)
        {
            _settings.OverspeedSpeedKmh = speed;
            ShowInlineHint(OverspeedSpeedHint);
            Commit();
            return;
        }

        OverspeedSpeedBox.Text = _settings.OverspeedSpeedKmh.ToString(CultureInfo.InvariantCulture);
    }

    private void OverspeedPathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        string text = OverspeedPathBox.Text.Trim();
        if (text.Length > 0 && System.IO.Path.GetExtension(text).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            _settings.OverspeedSoundPath = text;
            ShowInlineHint(OverspeedPathHint);
            Commit();
            return;
        }

        OverspeedPathBox.Text = _settings.OverspeedSoundPath;
    }

    private void OverspeedBrowse_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Filter = "MP3 文件 (*.mp3)|*.mp3",
            CheckFileExists = true,
        };

        // 默认定位到当前已选文件所在目录。
        string current = OverspeedPathBox.Text.Trim();
        if (current.Length > 0)
        {
            string fullPath = System.IO.Path.IsPathRooted(current)
                ? current
                : System.IO.Path.Combine(AppContext.BaseDirectory, current);
            string? directory = System.IO.Path.GetDirectoryName(fullPath);
            if (directory is not null && System.IO.Directory.Exists(directory))
            {
                dialog.InitialDirectory = directory;
            }
        }

        if (dialog.ShowDialog(this) == true)
        {
            OverspeedPathBox.Text = dialog.FileName;
            _settings.OverspeedSoundPath = dialog.FileName;
            ShowInlineHint(OverspeedPathHint);
            Commit();
        }
    }

    private void ToggleSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.AlwaysShowHud = AlwaysShowSwitch.IsChecked == true;
        _settings.ShowPerformancePanel = PerformanceSwitch.IsChecked == true;
        UpdateInstrumentStyleRowVisibility();
        _settings.ShowTirePanel = TireSwitch.IsChecked == true;
        _settings.ShowPerfPanel = PerfSwitch.IsChecked == true;

        Commit();
    }

    private void EndpointBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        string ip = IpBox.Text.Trim();

        bool ipValid = IsValidIpv4(ip);
        bool portValid = IsValidPort(PortBox.Text);

        if (!ipValid)
        {
            ShowErrorHint(IpSavedHint);
        }
        else
        {
            ClearHint(IpSavedHint);
        }

        if (!portValid)
        {
            ShowErrorHint(PortSavedHint);
        }
        else
        {
            ClearHint(PortSavedHint);
        }

        if (!ipValid || !portValid)
        {
            RestoreEndpointText();
            return;
        }

        if (!TryReadEndpoint(ip, PortBox.Text, out IPAddress unifiedIp, out int unifiedPort))
        {
            RestoreEndpointText();
            return;
        }

        bool ipChanged = !string.Equals(_settings.TelemetryIp, unifiedIp.ToString(), StringComparison.Ordinal);
        bool portChanged = _settings.TelemetryPort != unifiedPort;

        _settings.TelemetryIp = unifiedIp.ToString();
        _settings.TelemetryPort = unifiedPort;

        RestoreEndpointText();
        Commit();

        if (ipChanged)
        {
            ShowSavedHint(IpSavedHint);
        }

        if (portChanged)
        {
            ShowSavedHint(PortSavedHint);
        }
    }

    private void ShowSavedHint(TextBlock hint)
    {
        hint.Text = "✓ 已生效";
        hint.Foreground = OkHintBrush;
        hint.Opacity = 1;
        SetHintErrorState(hint, false);
        _endpointHintTimer.Stop();
        _endpointHintTimer.Start();
    }

    private void ShowErrorHint(TextBlock hint)
    {
        hint.Text = "IP/端口号不符合规范，未能修改！";
        hint.Foreground = ErrorHintBrush;
        hint.Opacity = 1;
        SetHintErrorState(hint, true);
        _endpointHintTimer.Stop();
    }

    private void ClearHint(TextBlock hint)
    {
        SetHintErrorState(hint, false);
        hint.Opacity = 0;
    }

    private void SetHintErrorState(TextBlock hint, bool isError)
    {
        if (ReferenceEquals(hint, IpSavedHint))
        {
            _ipHintIsError = isError;
        }
        else
        {
            _portHintIsError = isError;
        }
    }

    private void OnEndpointHintTimerTick(object? sender, EventArgs e)
    {
        _endpointHintTimer.Stop();

        if (!_ipHintIsError)
        {
            IpSavedHint.Opacity = 0;
        }

        if (!_portHintIsError)
        {
            PortSavedHint.Opacity = 0;
        }
    }

    private static bool IsValidIpv4(string text)
    {
        string trimmed = text.Trim();
        string[] parts = trimmed.Split('.');

        if (parts.Length != 4)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return IPAddress.TryParse(trimmed, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetwork;
    }

    private static bool IsValidPort(string text)
    {
        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            && port is >= 1 and <= 65535;
    }

    private void RestoreEndpointText()
    {
        IpBox.Text = _settings.TelemetryIp;
        PortBox.Text = FormatPort(_settings.TelemetryPort);
    }

    private void HotkeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HotkeyCombo.SelectedItem is not string candidate)
        {
            return;
        }

        if (!GlobalHotkeyService.TryApply(candidate))
        {
            _loading = true;
            HotkeyCombo.SelectedItem = _settings.HotkeyText;
            _loading = false;
            ShowHotkeyFailure();
            return;
        }

        _settings.HotkeyText = candidate;
        ShowHotkeyHint();
        Commit();

        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, null);
    }

    private void ShowStyleHint()
    {
        StyleHint.Text = "✓ 已生效";
        StyleHint.Foreground = OkHintBrush;
        StyleHint.Opacity = 1;
        _styleHintTimer.Stop();
        _styleHintTimer.Start();
    }

    private void OnStyleHintTimerTick(object? sender, EventArgs e)
    {
        _styleHintTimer.Stop();
        StyleHint.Opacity = 0;
    }

    private void ShowAspectHint()
    {
        AspectHint.Text = "✓ 已生效";
        AspectHint.Foreground = OkHintBrush;
        AspectHint.Opacity = 1;
        _aspectHintTimer.Stop();
        _aspectHintTimer.Start();
    }

    private void OnAspectHintTimerTick(object? sender, EventArgs e)
    {
        _aspectHintTimer.Stop();
        AspectHint.Opacity = 0;
    }

    private void ShowRefreshRateHint()
    {
        RefreshRateHint.Text = "✓ 已生效";
        RefreshRateHint.Foreground = OkHintBrush;
        RefreshRateHint.Opacity = 1;
        _refreshRateHintTimer.Stop();
        _refreshRateHintTimer.Start();
    }

    private void OnRefreshRateHintTimerTick(object? sender, EventArgs e)
    {
        _refreshRateHintTimer.Stop();
        RefreshRateHint.Opacity = 0;
    }

    private void ShowHotkeyHint()
    {
        HotkeyHint.Text = "✓ 已生效";
        HotkeyHint.Foreground = new SolidColorBrush(Color.FromRgb(0x3E, 0x9E, 0x5B));
        HotkeyHint.Opacity = 1;
        _hotkeyHintTimer.Stop();
        _hotkeyHintTimer.Start();
    }

    private void ShowHotkeyFailure()
    {
        HotkeyHint.Text = "✕ 已被占用";
        HotkeyHint.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x36, 0x2B));
        HotkeyHint.Opacity = 1;
        _hotkeyHintTimer.Stop();
        _hotkeyHintTimer.Start();
    }

    private void OnHotkeyHintTimerTick(object? sender, EventArgs e)
    {
        _hotkeyHintTimer.Stop();
        HotkeyHint.Opacity = 0;
    }

    private void PortBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(static c => !char.IsDigit(c));
    }

    private void AspectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AspectCombo.SelectedIndex < 0)
        {
            return;
        }

        _settings.HudAspectRatio = AspectCombo.SelectedIndex;
        UpdateCustomSizeVisibility();
        UpdateDerivedRatio();

        if (_loading)
        {
            return;
        }

        Commit();
        ShowAspectHint();

        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, null);
    }

    private void UpdateCustomSizeVisibility()
    {
        bool isCustom = AspectCombo.SelectedIndex == HudLayoutDefaults.AspectNames.Length - 1;
        CustomSizePanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CustomSizeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDerivedRatio();

        if (_loading || AspectCombo.SelectedIndex != HudLayoutDefaults.AspectNames.Length - 1)
        {
            return;
        }

        if (!int.TryParse(CustomWidthBox.Text, out int width) || width <= 0
            || !int.TryParse(CustomHeightBox.Text, out int height) || height <= 0)
        {
            return;
        }

        _settings.CustomAspectWidth = width;
        _settings.CustomAspectHeight = height;
        Commit();
    }

    private void UpdateDerivedRatio()
    {
        int width = _settings.CustomAspectWidth;
        int height = _settings.CustomAspectHeight;

        if (int.TryParse(CustomWidthBox.Text, out int parsedWidth) && parsedWidth > 0)
        {
            width = parsedWidth;
        }

        if (int.TryParse(CustomHeightBox.Text, out int parsedHeight) && parsedHeight > 0)
        {
            height = parsedHeight;
        }

        DerivedRatioText.Text = $"= {FormatRatio(width, height)}";
    }

    private static string FormatRatio(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return "16:9";
        }

        double ratio = (double)width / height;
        (double value, string name)[] candidates =
        [
            (16.0 / 9.0, "16:9"),
            (16.0 / 10.0, "16:10"),
            (21.0 / 9.0, "21:9"),
            (32.0 / 9.0, "32:9"),
            (48.0 / 9.0, "48:9"),
        ];

        foreach ((double value, string name) in candidates)
        {
            if (Math.Abs(ratio - value) < 0.01)
            {
                return name;
            }
        }

        return ratio.ToString("0.##", CultureInfo.InvariantCulture) + ":1";
    }

    private void OpenLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        Commit();

        HudLayoutWindow window = new(_settings, AspectCombo.SelectedIndex)
        {
            Owner = this,
        };
        window.ShowDialog();

        _settings = SettingsStore.Load();
    }

    private void OnLogTimerTick(object? sender, EventArgs e)
    {
        if (PageLog.Visibility == Visibility.Visible)
        {
            UpdateLogView();
            PerfLogStatusText.Text = PerfRecorder.GetStatusText();
        }
    }

    private void UpdateLogView()
    {
        LogBox.Text = Diagnostics.ReadTail(300);
        LogBox.ScrollToEnd();
    }

    private void RefreshLogButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateLogView();
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string directory = Diagnostics.LogDirectoryPath;
            System.IO.Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
        }
    }

    private void PerfLogSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.PerfLogEnabled = PerfLogSwitch.IsChecked == true;
        PerfRecorder.SetEnabled(_settings.PerfLogEnabled);
        Commit();
        PerfLogStatusText.Text = PerfRecorder.GetStatusText();
    }

    private void OpenPerfFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string directory = PerfRecorder.PerfDirectoryPath;
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
        }
    }

    private void MarkPerfButton_Click(object sender, RoutedEventArgs e)
    {
        PerfLogStatusText.Text = PerfRecorder.Mark()
            ? "已标记此刻 · " + PerfRecorder.GetStatusText()
            : "未开启记录，无法标记";
    }

    private void InitAboutPage()
    {
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "1.0.0";

        // 去掉 SourceLink 追加的 +commit 后缀，只显示版本号。
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            version = version[..plus];
        }

        AboutVersionText.Text = "版本 " + version;

        string imagePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Images", "whale-maid-full.jpg");
        if (File.Exists(imagePath))
        {
            AboutImage.Source = new BitmapImage(new Uri(imagePath));
        }
    }

    private void StartUpdateCheck()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => CheckForUpdatesAsync(manual: false));
        });
    }

    private async void CheckForUpdatesAsync(bool manual)
    {
        if (!_updateChecked)
        {
            UpdateInfo? info = await UpdateService.CheckAsync();
            _updateChecked = true;
            _pendingUpdate = info is not null && UpdateService.IsNewer(info) ? info : null;
        }

        if (_pendingUpdate is null)
        {
            AboutUpdateBadge.Visibility = Visibility.Collapsed;
            AboutUpdateStatus.Text = manual ? "已是最新版本" : string.Empty;
            AboutUpdateStatus.Foreground = NormalNavForeground;
            return;
        }

        AboutUpdateBadge.Visibility = Visibility.Visible;
        AboutUpdateStatus.Text = "发现新版本 v" + _pendingUpdate.Version;
        AboutUpdateStatus.Foreground = OkHintBrush;

        if (manual)
        {
            UpdateWindow window = new(_pendingUpdate)
            {
                Owner = this,
            };
            window.ShowDialog();
        }
    }

    private void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesAsync(manual: true);
    }

    private void AboutGithubCopy_Click(object sender, RoutedEventArgs e)
    {
        const string repoUrl = "https://github.com/FatFishNet/forza-fatfish-hud";

        try
        {
            Clipboard.SetText(repoUrl);

            if (Clipboard.GetText() == repoUrl)
            {
                AboutCopyHint.Text = "✓ 复制成功";
                AboutCopyHint.Foreground = OkHintBrush;
                AboutCopyHint.Opacity = 1;
                _aboutCopyHintTimer.Stop();
                _aboutCopyHintTimer.Start();
            }
            else
            {
                Diagnostics.Write("复制地址校验失败：剪贴板内容不一致");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write("复制地址失败: " + ex.Message);
        }
    }

    private void OnAboutCopyHintTimerTick(object? sender, EventArgs e)
    {
        _aboutCopyHintTimer.Stop();
        AboutCopyHint.Opacity = 0;
    }

    private readonly Dictionary<TextBlock, DispatcherTimer> _inlineHintTimers = [];

    private void ShowInlineHint(TextBlock hint)
    {
        hint.Text = "✓ 已生效";
        hint.Foreground = OkHintBrush;
        hint.Opacity = 1;
        if (!_inlineHintTimers.TryGetValue(hint, out DispatcherTimer? timer))
        {
            timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                hint.Opacity = 0;
            };
            _inlineHintTimers[hint] = timer;
        }

        timer.Stop();
        timer.Start();
    }

    private void Commit()
    {
        SettingsStore.Save(_settings);
        AppServices.ApplySettings(_settings);
    }

    private static string FormatPort(int port)
    {
        return port is >= 1 and <= 65535 ? port.ToString(CultureInfo.InvariantCulture) : DefaultPort.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryReadEndpoint(string ip, string portText, out IPAddress address, out int port)
    {
        address = IPAddress.None;
        port = 0;

        string trimmedIp = ip.Trim();
        string[] parts = trimmedIp.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        if (!IPAddress.TryParse(trimmedIp, out IPAddress? parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        if (!int.TryParse(portText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPort)
            || parsedPort is < 1 or > 65535)
        {
            return false;
        }

        address = parsed;
        port = parsedPort;
        return true;
    }
}
