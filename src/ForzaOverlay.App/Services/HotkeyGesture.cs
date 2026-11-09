using System.Text;
using System.Windows.Input;

namespace ForzaOverlay.App.Services;

public static class HotkeyGesture
{
    private static readonly Dictionary<string, ModifierKeys> ModifierMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = ModifierKeys.Control,
        ["Shift"] = ModifierKeys.Shift,
        ["Alt"] = ModifierKeys.Alt,
        ["Win"] = ModifierKeys.Windows,
    };

    public static string Format(ModifierKeys modifiers, Key key)
    {
        StringBuilder builder = new();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            builder.Append("Ctrl+");
        }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            builder.Append("Shift+");
        }
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            builder.Append("Alt+");
        }
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            builder.Append("Win+");
        }
        builder.Append(key);
        return builder.ToString();
    }

    public static bool TryParse(string? text, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < tokens.Length - 1; i++)
        {
            if (!ModifierMap.TryGetValue(tokens[i], out ModifierKeys modifier))
            {
                return false;
            }
            modifiers |= modifier;
        }

        if (!Enum.TryParse(tokens[^1], ignoreCase: true, out key) || key == Key.None)
        {
            key = Key.None;
            return false;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            key = Key.None;
            return false;
        }

        bool isFunctionKey = key is >= Key.F1 and <= Key.F24;
        return modifiers != ModifierKeys.None || isFunctionKey;
    }
}
