using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace Desktype;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0xD357;
    private const int WmHotkey = 0x0312;
    private const uint NoModifiers = 0;
    private const uint DefaultHotkey = 0x7A; // F11
    private CancellationTokenSource? _typingCancellation;
    private HwndSource? _source;
    private uint _hotkeyVirtualKey = DefaultHotkey;
    private bool _hotkeyRegistered;
    private bool _isCapturingHotkey;
    private bool _isTyping;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public MainWindow()
    {
        InitializeComponent();
        InputText.Text = "Hello! This text was typed by Desktype.";
        UpdateCharacterCount();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowProc);
        RegisterCurrentHotkey();
    }

    private void RegisterCurrentHotkey()
    {
        if (_source is null) return;

        _hotkeyRegistered = RegisterHotKey(_source.Handle, HotkeyId, NoModifiers, _hotkeyVirtualKey);
        HotkeyButton.Content = $"Change hotkey · {HotkeyName(_hotkeyVirtualKey)}";

        if (_hotkeyRegistered)
        {
            StatusText.Text = "Ready when you are.";
            StatusDetail.Text = $"Press {HotkeyName(_hotkeyVirtualKey)} in any app to type instantly.";
        }
        else
        {
            StatusText.Text = "Couldn't register the hotkey.";
            StatusDetail.Text = "That key may already be in use. Click Change hotkey to choose another.";
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            if (!_isCapturingHotkey)
                _ = TypeTextAsync();
        }

        return IntPtr.Zero;
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = true;
        HotkeyButton.Content = "Press a function key…";
        StatusText.Text = "Choose your hotkey.";
        StatusDetail.Text = "Press F1–F11 or F13–F24. Escape cancels.";
        Activate();
        Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey) return;

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            _isCapturingHotkey = false;
            HotkeyButton.Content = $"Change hotkey · {HotkeyName(_hotkeyVirtualKey)}";
            StatusText.Text = "Hotkey change cancelled.";
            StatusDetail.Text = $"Current hotkey: {HotkeyName(_hotkeyVirtualKey)}";
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key < Key.F1 || key > Key.F24 || key == Key.F12)
        {
            StatusText.Text = "Choose a function key.";
            StatusDetail.Text = "Use F1–F11 or F13–F24. F12 is reserved by Windows for debugging.";
            return;
        }

        var newVirtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        var previousVirtualKey = _hotkeyVirtualKey;

        if (_source is null)
        {
            StatusText.Text = "Couldn't change the hotkey.";
            return;
        }

        if (_hotkeyRegistered)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _hotkeyRegistered = false;
        }

        if (RegisterHotKey(_source.Handle, HotkeyId, NoModifiers, newVirtualKey))
        {
            _hotkeyVirtualKey = newVirtualKey;
            _hotkeyRegistered = true;
            _isCapturingHotkey = false;
            HotkeyButton.Content = $"Change hotkey · {HotkeyName(_hotkeyVirtualKey)}";
            StatusText.Text = "Hotkey updated.";
            StatusDetail.Text = $"Press {HotkeyName(_hotkeyVirtualKey)} in any app to type instantly.";
        }
        else
        {
            _hotkeyRegistered = RegisterHotKey(_source.Handle, HotkeyId, NoModifiers, previousVirtualKey);
            _isCapturingHotkey = false;
            HotkeyButton.Content = $"Change hotkey · {HotkeyName(_hotkeyVirtualKey)}";
            StatusText.Text = "That key is already in use.";
            StatusDetail.Text = _hotkeyRegistered
                ? $"Kept the previous hotkey: {HotkeyName(previousVirtualKey)}."
                : "The previous hotkey couldn't be restored. Try another function key.";
        }
    }

    private static string HotkeyName(uint virtualKey) =>
        KeyInterop.KeyFromVirtualKey((int)virtualKey).ToString();

    private void InputText_TextChanged(object sender, TextChangedEventArgs e) => UpdateCharacterCount();

    private void UpdateCharacterCount() =>
        CharacterCount.Text = $"{InputText.Text.Length:N0} character{(InputText.Text.Length == 1 ? "" : "s")}";

    private async Task TypeTextAsync()
    {
        if (_isTyping) return;

        var text = InputText.Text;
        if (string.IsNullOrEmpty(text))
        {
            StatusText.Text = "Add some text first.";
            StatusDetail.Text = "The text box above is empty.";
            return;
        }

        _isTyping = true;
        _typingCancellation = new CancellationTokenSource();
        var token = _typingCancellation.Token;
        InputText.IsEnabled = false;
        HotkeyButton.IsEnabled = false;
        StopButton.IsEnabled = true;

        try
        {
            StatusText.Text = "Typing…";
            StatusDetail.Text = $"Started instantly with {HotkeyName(_hotkeyVirtualKey)}. Press Stop to cancel.";
            foreach (var character in text)
            {
                token.ThrowIfCancellationRequested();
                if (!NativeTyping.TypeCharacter(character))
                    throw new InvalidOperationException("Windows could not send keyboard input.");
                await Task.Delay(8, token);
            }

            StatusText.Text = "Done!";
            StatusDetail.Text = "Your text was sent to the focused field.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Stopped.";
            StatusDetail.Text = "Typing was cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't finish typing.";
            StatusDetail.Text = ex.Message;
        }
        finally
        {
            _typingCancellation?.Dispose();
            _typingCancellation = null;
            _isTyping = false;
            InputText.IsEnabled = true;
            HotkeyButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) =>
        _typingCancellation?.Cancel();

    protected override void OnClosing(CancelEventArgs e)
    {
        _typingCancellation?.Cancel();
        if (_source is not null)
        {
            _source.RemoveHook(WindowProc);
            if (_hotkeyRegistered)
                UnregisterHotKey(_source.Handle, HotkeyId);
        }
        base.OnClosing(e);
    }
}

internal static class NativeTyping
{
    private const uint InputKeyboard = 1;
    private const uint KeyeventfUnicode = 0x0004;
    private const uint KeyeventfKeyup = 0x0002;
    private const ushort VkReturn = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static bool TypeCharacter(char character)
    {
        if (character == '\r') return true;
        if (character == '\n')
        {
            var enterInputs = new[]
            {
                CreateVirtualKeyInput(VkReturn, 0),
                CreateVirtualKeyInput(VkReturn, KeyeventfKeyup)
            };
            return SendInput((uint)enterInputs.Length, enterInputs, Marshal.SizeOf<INPUT>()) == (uint)enterInputs.Length;
        }

        var inputs = new[]
        {
            CreateInput(character, KeyeventfUnicode),
            CreateInput(character, KeyeventfUnicode | KeyeventfKeyup)
        };

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == (uint)inputs.Length;
    }

    private static INPUT CreateVirtualKeyInput(ushort key, uint flags) => new()
    {
        type = InputKeyboard,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = key,
                wScan = 0,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = UIntPtr.Zero
            }
        }
    };

    private static INPUT CreateInput(char character, uint flags) => new()
    {
        type = InputKeyboard,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = character,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = UIntPtr.Zero
            }
        }
    };
}