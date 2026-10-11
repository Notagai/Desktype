using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Desktype;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _typingCancellation;
    private bool _isTyping;

    public MainWindow()
    {
        InitializeComponent();
        InputText.Text = "Hello! This text was typed by Desktype.";
        UpdateCharacterCount();
    }

    private void InputText_TextChanged(object sender, TextChangedEventArgs e) => UpdateCharacterCount();

    private void UpdateCharacterCount() =>
        CharacterCount.Text = $"{InputText.Text.Length:N0} character{(InputText.Text.Length == 1 ? "" : "s")}";

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTyping) return;
        var text = InputText.Text;
        if (string.IsNullOrEmpty(text))
        {
            StatusText.Text = "Add some text first.";
            StatusDetail.Text = "The text box above is empty.";
            InputText.Focus();
            return;
        }

        _isTyping = true;
        _typingCancellation = new CancellationTokenSource();
        var token = _typingCancellation.Token;
        StartButton.IsEnabled = false;
        InputText.IsEnabled = false;
        StopButton.IsEnabled = true;

        try
        {
            for (var seconds = 3; seconds > 0; seconds--)
            {
                StatusText.Text = $"Switch to your target app… {seconds}";
                StatusDetail.Text = "Click where you want the text to appear.";
                await Task.Delay(1000, token);
            }

            StatusText.Text = "Typing…";
            StatusDetail.Text = "Press Stop to cancel.";
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
            StartButton.IsEnabled = true;
            InputText.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) =>
        _typingCancellation?.Cancel();

    protected override void OnClosing(CancelEventArgs e)
    {
        _typingCancellation?.Cancel();
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
        if (character == '\\r') return true;
        if (character == '\\n')
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
