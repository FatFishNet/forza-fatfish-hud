using System.Windows.Input;
using NHotkey;
using NHotkey.Wpf;

namespace ForzaOverlay.App.Services;

public static class GlobalHotkeyService
{
    private const string ToggleHudHotkeyName = "ForzaOverlay.ToggleHud";

    public static event Action? ToggleHudRequested;

    public static bool TryApply(string hotkeyText)
    {
        if (!HotkeyGesture.TryParse(hotkeyText, out Key key, out ModifierKeys modifiers))
        {
            return false;
        }

        try
        {
            HotkeyManager.Current.AddOrReplace(ToggleHudHotkeyName, key, modifiers, OnToggleHud);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Clear()
    {
        try
        {
            HotkeyManager.Current.Remove(ToggleHudHotkeyName);
        }
        catch (Exception)
        {
        }
    }

    private static void OnToggleHud(object? sender, HotkeyEventArgs e)
    {
        e.Handled = true;
        ToggleHudRequested?.Invoke();
    }
}
