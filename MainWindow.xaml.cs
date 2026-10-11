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
        InputText.Text = "";
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
        UpdateHotkeyButton();

        StatusText.Text = _hotkeyRegistered
            ? $"Ready · {HotkeyName(_hotkeyVirtualKey)} starts typing"
            : "Hotkey busy — choose another";
    }

    private void UpdateHotkeyButton() =>
        HotkeyButton.Content = _isCapturingHotkey
            ? "Press a key…"
            : $"Hotkey: {HotkeyName(_hotkeyVirtualKey)}";

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            if (_isCapturingHotkey) return IntPtr.Zero;

            if (_isTyping)
                _typingCancellation?.Cancel();
            else
                _ = TypeTextAsync();
        }

        return IntPtr.Zero;
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = true;
        UpdateHotkeyButton();
        StatusText.Text = "Press a function key · Esc cancels";
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
            UpdateHotkeyButton();
            StatusText.Text = $"Ready · {HotkeyName(_hotkeyVirtualKey)} starts typing";
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if ((int)key < (int)Key.F1 || (int)key > (int)Key.F24)
        {
            StatusText.Text = "Choose a function key (F1–F24)";
            return;
        }

        if (_source is null)
        {
            _isCapturingHotkey = false;
            UpdateHotkeyButton();
            StatusText.Text = "Couldn't change hotkey";
            return;
        }

        var newVirtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        var previousVirtualKey = _hotkeyVirtualKey;

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
            UpdateHotkeyButton();
            StatusText.Text = $"Ready · {HotkeyName(_hotkeyVirtualKey)} starts typing";
        }
        else
        {
            _hotkeyRegistered = RegisterHotKey(_source.Handle, HotkeyId, NoModifiers, previousVirtualKey);
            _isCapturingHotkey = false;
            UpdateHotkeyButton();
            StatusText.Text = key == Key.F12
                ? "Windows may reserve F12; try another key"
                : "Key is already in use";
        }
    }

    private static string HotkeyName(uint virtualKey) =>
        KeyInterop.KeyFromVirtualKey((int)virtualKey).ToString();

    private void InputText_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Keep the minimal interface; no extra status UI is needed here.
    }

    private async Task TypeTextAsync()
    {
        if (_isTyping) return;

        var text = InputText.Text;
        if (string.IsNullOrEmpty(text))
        {
            StatusText.Text = "Enter text first";
            return;
        }

        if (!int.TryParse(WpmInput.Text, out var wpm) || wpm < 1 || wpm > 1000)
        {
            StatusText.Text = "WPM must be 1–1000";
            WpmInput.Focus();
            WpmInput.SelectAll();
            return;
        }

        // Standard typing-test convention: 1 word = 5 characters.
        // Character timing varies inside short bursts, with brief pauses at burst
        // boundaries and after punctuation instead of a perfectly even metronome.
        var targetCharacterInterval = 12000.0 / wpm;
        // Fixed-length pauses dominate at high WPM, so shrink pauses nonlinearly
        // as the requested speed rises. This is calibrated against observed results:
        // about 150 WPM at a setting of 180, and about 260 WPM at a setting of 839
        // with the previous fixed pauses.
        var pauseScale = Math.Min(1.0, Math.Pow(180.0 / wpm, 1.5));
        var burstLength = Random.Shared.Next(4, 10);
        var charactersInBurst = 0;
        _isTyping = true;
        _typingCancellation = new CancellationTokenSource();
        var token = _typingCancellation.Token;
        HotkeyButton.IsEnabled = false;
        WpmInput.IsEnabled = false;

        try
        {
            StatusText.Text = $"Typing · {wpm} WPM";
            foreach (var character in text)
            {
                token.ThrowIfCancellationRequested();
                if (!NativeTyping.TypeCharacter(character))
                    throw new InvalidOperationException("Windows could not send keyboard input.");

                charactersInBurst++;
                var speedVariation = 0.68 + (Random.Shared.NextDouble() * 0.62);
                var characterDelay = Math.Max(1, (int)Math.Round(targetCharacterInterval * 0.68 * speedVariation));
                await Task.Delay(characterDelay, token);

                if (character is '.' or ',' or '!' or '?' or ';' or ':')
                {
                    await Task.Delay(Math.Max(1, (int)Math.Round(Random.Shared.Next(110, 291) * pauseScale)), token);
                }
                else if (character == ' ')
                {
                    var spacePause = Random.Shared.Next(0, 36);
                    if (spacePause > 0)
                        await Task.Delay(Math.Max(1, (int)Math.Round(spacePause * pauseScale)), token);
                }

                if (charactersInBurst >= burstLength)
                {
                    await Task.Delay(Math.Max(1, (int)Math.Round(Random.Shared.Next(90, 251) * pauseScale)), token);
                    charactersInBurst = 0;
                    burstLength = Random.Shared.Next(4, 10);
                }
            }

            StatusText.Text = "Done";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Stopped";
        }
        catch (Exception)
        {
            StatusText.Text = "Typing failed";
        }
        finally
        {
            _typingCancellation?.Dispose();
            _typingCancellation = null;
            _isTyping = false;
            HotkeyButton.IsEnabled = true;
            WpmInput.IsEnabled = true;
        }
    }

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