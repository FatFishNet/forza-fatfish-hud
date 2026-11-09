using System.Runtime.InteropServices;
using ForzaOverlay.Core.State;

namespace ForzaOverlay.App.Services;

/// <summary>
/// 与 WPF HUD 相同的显示约束：遥测开关、游戏窗口识别、前台、驾驶状态、时速门限。
/// </summary>
public static class HudVisibilityRules
{
    private const int GaRootOwner = 3;
    private const int GwlHwndParent = -8;

    public static bool Evaluate(IntPtr gameHwnd, bool isDriving, double speedKmh, bool speedGateOpen, out string reason)
    {
        if (!TelemetryStateManager.Instance.IsTelemetryEnabled)
        {
            reason = "快捷键已关闭遥测";
            return false;
        }

        if (gameHwnd == IntPtr.Zero)
        {
            reason = "未识别到游戏窗口";
            return false;
        }

        if (!IsWindow(gameHwnd))
        {
            reason = "游戏窗口已关闭";
            return false;
        }

        if (IsIconic(gameHwnd))
        {
            reason = "游戏窗口已最小化";
            return false;
        }

        bool alwaysShow = AppServices.CurrentSettings?.AlwaysShowHud == true;
        bool foreground = IsGameWindowForeground(gameHwnd);

        if (alwaysShow)
        {
            if (!foreground)
            {
                reason = "游戏不在前台（始终显示模式）";
                return false;
            }

            reason = "始终显示模式";
            return true;
        }

        if (!isDriving)
        {
            reason = "非驾驶状态（IsRaceOn=0）";
            return false;
        }

        if (!foreground)
        {
            reason = "游戏不在前台";
            return false;
        }

        if (!speedGateOpen)
        {
            reason = $"时速 {speedKmh:F1} km/h 低于显示门槛";
            return false;
        }

        reason = "满足全部显示条件";
        return true;
    }

    private static bool IsGameWindowForeground(IntPtr gameHwnd)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        if (foreground == gameHwnd)
        {
            return true;
        }

        if (GetAncestor(foreground, GaRootOwner) == gameHwnd)
        {
            return true;
        }

        if (GetWindowLongPtr(foreground, GwlHwndParent) == gameHwnd)
        {
            return true;
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, int flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
}
