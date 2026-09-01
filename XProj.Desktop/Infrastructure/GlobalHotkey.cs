using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ProjectManager.Wpf.Infrastructure;

internal sealed class GlobalHotkeyRegistration : IDisposable
{
    private const int HotkeyId = 0x5850;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private Window? _window;
    private HwndSource? _source;
    private bool _registered;

    public event EventHandler? Pressed;

    public bool TryRegister(Window window, string? gestureText, out string? error)
    {
        Unregister();
        error = null;

        if (string.IsNullOrWhiteSpace(gestureText))
        {
            return true;
        }

        if (!GlobalHotkey.TryParseGesture(gestureText, out var gesture, out error))
        {
            return false;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            error = "主窗口句柄尚未创建。";
            return false;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        if (virtualKey == 0)
        {
            error = "快捷键主键无效。";
            return false;
        }

        _source = HwndSource.FromHwnd(handle);
        if (_source is null)
        {
            error = "无法接收系统快捷键消息。";
            return false;
        }

        _source.AddHook(WndProc);
        if (!RegisterHotKey(handle, HotkeyId, ToNativeModifiers(gesture.Modifiers) | ModNoRepeat, virtualKey))
        {
            var code = Marshal.GetLastPInvokeError();
            _source.RemoveHook(WndProc);
            _source = null;
            error = code == ErrorHotkeyAlreadyRegistered
                ? "该快捷键已被其他程序占用。"
                : new Win32Exception(code).Message;
            return false;
        }

        _window = window;
        _registered = true;
        return true;
    }

    public void Unregister()
    {
        if (_registered && _window is not null)
        {
            var handle = new WindowInteropHelper(_window).Handle;
            if (handle != IntPtr.Zero)
            {
                _ = UnregisterHotKey(handle, HotkeyId);
            }
        }

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _window = null;
        _registered = false;
    }

    public void Dispose() => Unregister();

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        var native = 0u;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            native |= ModControl;
        }
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            native |= ModAlt;
        }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            native |= ModShift;
        }
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            native |= ModWin;
        }

        return native;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}

internal static class GlobalHotkey
{
    public static bool TryParseGesture(string? text, out GlobalHotkeyGesture gesture, out string? error)
    {
        gesture = default;
        if (!TryNormalizeGesture(text, out _, out error, out gesture))
        {
            return false;
        }

        if (gesture.Key == Key.None)
        {
            error = "快捷键不能为空。";
            return false;
        }

        return true;
    }

    public static bool TryNormalizeGesture(string? text, out string normalized, out string? error) =>
        TryNormalizeGesture(text, out normalized, out error, out _);

    public static bool TryCreateGesture(ModifierKeys modifiers, Key key, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (IsModifierKey(key))
        {
            error = "请再按一个主键。";
            return false;
        }

        if (!IsSupportedKey(key))
        {
            error = "该按键不能作为全局快捷键。";
            return false;
        }

        if (modifiers == ModifierKeys.None)
        {
            error = "全局快捷键至少需要一个修饰键。";
            return false;
        }

        normalized = FormatGesture(new GlobalHotkeyGesture(modifiers, key));
        return true;
    }

    private static bool TryNormalizeGesture(string? text, out string normalized, out string? error, out GlobalHotkeyGesture gesture)
    {
        normalized = string.Empty;
        error = null;
        gesture = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var modifiers = ModifierKeys.None;
        Key? mainKey = null;
        foreach (var rawToken in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseModifier(rawToken, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (mainKey.HasValue)
            {
                error = "快捷键只能包含一个主键。";
                return false;
            }

            if (!TryParseKey(rawToken, out var key) || !IsSupportedKey(key))
            {
                error = $"无法识别快捷键主键：{rawToken}";
                return false;
            }

            mainKey = key;
        }

        if (mainKey is null)
        {
            error = "快捷键需要一个主键。";
            return false;
        }

        if (modifiers == ModifierKeys.None)
        {
            error = "全局快捷键至少需要一个修饰键。";
            return false;
        }

        gesture = new GlobalHotkeyGesture(modifiers, mainKey.Value);
        normalized = FormatGesture(gesture);
        return true;
    }

    private static bool TryParseModifier(string token, out ModifierKeys modifier)
    {
        modifier = token.Trim().ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => ModifierKeys.Control,
            "ALT" => ModifierKeys.Alt,
            "SHIFT" => ModifierKeys.Shift,
            "WIN" or "WINDOWS" or "META" => ModifierKeys.Windows,
            _ => ModifierKeys.None
        };

        return modifier != ModifierKeys.None;
    }

    private static bool TryParseKey(string token, out Key key)
    {
        key = Key.None;
        var value = token.Trim();
        if (value.Length == 1)
        {
            var character = char.ToUpperInvariant(value[0]);
            if (character is >= 'A' and <= 'Z')
            {
                key = Key.A + (character - 'A');
                return true;
            }
            if (character is >= '0' and <= '9')
            {
                key = Key.D0 + (character - '0');
                return true;
            }
        }

        if (value.Length is 2 or 3 && value[0] is 'F' or 'f' && int.TryParse(value[1..], out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            key = Key.F1 + (functionKey - 1);
            return true;
        }

        if (value.StartsWith("Num", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value[3..], out var numPadKey) && numPadKey is >= 0 and <= 9)
        {
            key = Key.NumPad0 + numPadKey;
            return true;
        }

        key = value.ToUpperInvariant() switch
        {
            "SPACE" => Key.Space,
            "ESC" or "ESCAPE" => Key.Escape,
            "ENTER" or "RETURN" => Key.Enter,
            "BACKSPACE" or "BACK" => Key.Back,
            "DEL" or "DELETE" => Key.Delete,
            "INS" or "INSERT" => Key.Insert,
            "PGUP" or "PAGEUP" => Key.PageUp,
            "PGDN" or "PAGEDOWN" => Key.PageDown,
            _ => Key.None
        };
        if (key != Key.None)
        {
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out key) && IsSupportedKey(key);
    }

    private static bool IsSupportedKey(Key key) =>
        key != Key.None && key != Key.System && key != Key.ImeProcessed && key != Key.DeadCharProcessed && !IsModifierKey(key);

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string FormatGesture(GlobalHotkeyGesture gesture)
    {
        var parts = new List<string>();
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(FormatKey(gesture.Key));
        return string.Join('+', parts);
    }

    private static string FormatKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return ((char)('A' + key - Key.A)).ToString();
        }
        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((char)('0' + key - Key.D0)).ToString();
        }
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return $"Num{key - Key.NumPad0}";
        }

        return key switch
        {
            Key.Escape => "Esc",
            Key.Back => "Backspace",
            Key.Delete => "Del",
            Key.Insert => "Ins",
            Key.PageUp => "PgUp",
            Key.PageDown => "PgDn",
            _ => key.ToString()
        };
    }
}

internal readonly record struct GlobalHotkeyGesture(ModifierKeys Modifiers, Key Key);
