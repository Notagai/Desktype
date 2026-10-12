using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Desktype");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    private CancellationTokenSource? _typingCancellation;
    private HwndSource? _source;
    private uint _hotkeyVirtualKey = DefaultHotkey;
    private bool _hotkeyRegistered;
    private bool _isCapturingHotkey;
    private bool _isTyping;
    // Current character index for this in-memory typing session; never persisted.
    private int _typingPosition;
    private IntPtr _typingTargetWindow;
    private bool _pauseRequested;
    private bool _isPaused;
    private bool _loadingSettings;

    private enum TypoMode
    {
        None,
        Corrected,
        Left
    }

    private sealed class AppSettings
    {
        public string Text { get; set; } = "";
        public string Wpm { get; set; } = "60";
        public string TyposPer100Words { get; set; } = "3";
        public string TypoMode { get; set; } = nameof(Desktype.MainWindow.TypoMode.Corrected);
        public uint HotkeyVirtualKey { get; set; } = DefaultHotkey;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public MainWindow()
    {
        // TextChanged events can fire during InitializeComponent, before all
        // named controls (including WpmInput) have been initialized.
        _loadingSettings = true;
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            AppSettings settings;
            try
            {
                settings = File.Exists(SettingsPath)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings()
                    : new AppSettings();
            }
            catch (Exception ex)
            {
                settings = new AppSettings();
                StatusText.Text = $"Couldn't read saved settings: {ex.Message}";
            }

            InputText.Text = settings.Text ?? "";
            WpmInput.Text = settings.Wpm ?? "60";
            TyposPer100Input.Text = settings.TyposPer100Words ?? "3";
            _hotkeyVirtualKey = IsFunctionKeyVirtualCode(settings.HotkeyVirtualKey)
                ? settings.HotkeyVirtualKey
                : DefaultHotkey;

            switch (settings.TypoMode)
            {
                case nameof(TypoMode.None):
                    NoTyposRadio.IsChecked = true;
                    break;
                case nameof(TypoMode.Left):
                    LeftTyposRadio.IsChecked = true;
                    break;
                default:
                    CorrectedTyposRadio.IsChecked = true;
                    break;
            }
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private static bool IsFunctionKeyVirtualCode(uint key) => key >= 0x70 && key <= 0x87;

    private void SaveSettings()
    {
        if (_loadingSettings) return;

        var settings = new AppSettings
        {
            Text = InputText.Text,
            Wpm = WpmInput.Text,
            TyposPer100Words = TyposPer100Input.Text,
            TypoMode = GetTypoMode().ToString(),
            HotkeyVirtualKey = _hotkeyVirtualKey
        };

        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Keep the app usable, but make persistence failures visible instead of silently losing data.
            StatusText.Text = $"Couldn't save settings: {ex.Message}";
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveSettings();
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
            SaveSettings();
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

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isTyping) return;

        if (_isPaused)
        {
            // Clicking Resume gives Desktype focus. Return focus to the original
            // destination before sending the next character.
            if (_typingTargetWindow != IntPtr.Zero)
                SetForegroundWindow(_typingTargetWindow);
            _isPaused = false;
            PauseButton.Content = "Pause";
            StatusText.Text = "Resuming…";
        }
        else if (!_pauseRequested)
        {
            _pauseRequested = true;
            PauseButton.Content = "Pausing…";
            StatusText.Text = "Pausing after current character…";
        }
    }

    private void InputText_TextChanged(object sender, TextChangedEventArgs e) => SaveSettings();

    private void WpmInput_TextChanged(object sender, TextChangedEventArgs e) => SaveSettings();

    private void TyposPer100Input_TextChanged(object sender, TextChangedEventArgs e) => SaveSettings();

    private void TypoMode_Changed(object sender, RoutedEventArgs e) => SaveSettings();

    private TypoMode GetTypoMode()
    {
        if (NoTyposRadio.IsChecked == true) return TypoMode.None;
        if (LeftTyposRadio.IsChecked == true) return TypoMode.Left;
        return TypoMode.Corrected;
    }

    private async Task TypeTextAsync()
    {
        if (_isTyping) return;

        // The global hotkey starts typing without activating Desktype, so this
        // captures the destination that should receive input after a pause.
        _typingTargetWindow = GetForegroundWindow();
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

        if (!int.TryParse(TyposPer100Input.Text, out var typosPer100) || typosPer100 < 0 || typosPer100 > 100)
        {
            StatusText.Text = "Typos per 100 words must be 0–100";
            TyposPer100Input.Focus();
            TyposPer100Input.SelectAll();
            return;
        }

        SaveSettings();
        var typoMode = GetTypoMode();
        var typoPositions = ChooseTypoPositions(text, typosPer100, typoMode != TypoMode.None);
        var targetCharacterInterval = 12000.0 / wpm;
        var pauseScale = Math.Min(1.0, Math.Pow(180.0 / wpm, 1.5));
        var burstLength = Random.Shared.Next(4, 10);
        var charactersInBurst = 0;
        _typingPosition = 0;
        _isTyping = true;
        _pauseRequested = false;
        _isPaused = false;
        PauseButton.Content = "Pause";
        PauseButton.IsEnabled = true;
        _typingCancellation = new CancellationTokenSource();
        var token = _typingCancellation.Token;
        HotkeyButton.IsEnabled = false;
        WpmInput.IsEnabled = false;
        TyposPer100Input.IsEnabled = false;
        NoTyposRadio.IsEnabled = false;
        CorrectedTyposRadio.IsEnabled = false;
        LeftTyposRadio.IsEnabled = false;

        try
        {
            StatusText.Text = $"Typing · {wpm} WPM";
            while (_typingPosition < text.Length)
            {
                token.ThrowIfCancellationRequested();
                if (_pauseRequested || _isPaused)
                {
                    _pauseRequested = false;
                    _isPaused = true;
                    PauseButton.Content = "Resume";
                    StatusText.Text = "Paused · press Resume to continue";
                    while (_isPaused)
                        await Task.Delay(50, token);

                    PauseButton.Content = "Pause";
                    StatusText.Text = $"Typing · {wpm} WPM";
                }
                var characterIndex = _typingPosition;
                var character = text[characterIndex];
                if (typoPositions.Contains(characterIndex))
                {
                    var wrongCharacter = GetTypoCharacter(character);
                    if (typoMode == TypoMode.Corrected)
                    {
                        if (!NativeTyping.TypeCharacter(wrongCharacter))
                            throw new InvalidOperationException("Windows could not send keyboard input.");
                        await Task.Delay(Random.Shared.Next(75, 181), token);
                        if (!NativeTyping.TypeBackspace())
                            throw new InvalidOperationException("Windows could not send Backspace.");
                        await Task.Delay(Random.Shared.Next(35, 91), token);
                    }
                    else if (typoMode == TypoMode.Left)
                    {
                        character = wrongCharacter;
                    }
                }

                if (!NativeTyping.TypeCharacter(character))
                    throw new InvalidOperationException("Windows could not send keyboard input.");

                // Advance only after this source character was sent successfully.
                _typingPosition++;
                StatusText.Text = $"Typing · {wpm} WPM · {_typingPosition}/{text.Length}";
                charactersInBurst++;
                var speedVariation = 0.68 + (Random.Shared.NextDouble() * 0.62);
                var characterDelay = Math.Max(1, (int)Math.Round(targetCharacterInterval * 0.68 * speedVariation));
                await Task.Delay(characterDelay, token);

                if (character is '.' or ',' or '!' or '?' or ';' or ':')
                    await Task.Delay(Math.Max(1, (int)Math.Round(Random.Shared.Next(110, 291) * pauseScale)), token);
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
            _typingTargetWindow = IntPtr.Zero;
            _typingPosition = 0;
            _pauseRequested = false;
            _isPaused = false;
            PauseButton.Content = "Pause";
            PauseButton.IsEnabled = false;
            HotkeyButton.IsEnabled = true;
            WpmInput.IsEnabled = true;
            TyposPer100Input.IsEnabled = true;
            NoTyposRadio.IsEnabled = true;
            CorrectedTyposRadio.IsEnabled = true;
            LeftTyposRadio.IsEnabled = true;
        }
    }

    private static HashSet<int> ChooseTypoPositions(string text, int typosPer100, bool enabled)
    {
        var positions = new HashSet<int>();
        if (!enabled || typosPer100 <= 0) return positions;

        var wordCount = Regex.Matches(text, @"\b[\p{L}\p{N}']+\b").Count;
        if (wordCount == 0) return positions;

        var expectedCount = typosPer100 * wordCount / 100.0;
        var typoCount = (int)Math.Floor(expectedCount);
        if (Random.Shared.NextDouble() < expectedCount - typoCount) typoCount++;
        var candidates = Enumerable.Range(0, text.Length).Where(i => char.IsLetter(text[i])).ToList();
        typoCount = Math.Min(typoCount, candidates.Count);

        for (var i = 0; i < typoCount; i++)
        {
            var pick = Random.Shared.Next(i, candidates.Count);
            (candidates[i], candidates[pick]) = (candidates[pick], candidates[i]);
            positions.Add(candidates[i]);
        }

        return positions;
    }

    private static char GetTypoCharacter(char original)
    {
        const string row1 = "qwertyuiop";
        const string row2 = "asdfghjkl";
        const string row3 = "zxcvbnm";
        var lower = char.ToLowerInvariant(original);
        var row = row1.Contains(lower) ? row1 : row2.Contains(lower) ? row2 : row3.Contains(lower) ? row3 : "";
        if (row.Length == 0) return original;

        var index = row.IndexOf(lower);
        var neighbors = new List<char>(2);
        if (index > 0) neighbors.Add(row[index - 1]);
        if (index < row.Length - 1) neighbors.Add(row[index + 1]);
        var typo = neighbors[Random.Shared.Next(neighbors.Count)];
        return char.IsUpper(original) ? char.ToUpperInvariant(typo) : typo;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveSettings();
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
    private const ushort VkBack = 0x08;

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

    public static bool TypeBackspace()
    {
        var inputs = new[]
        {
            CreateVirtualKeyInput(VkBack, 0),
            CreateVirtualKeyInput(VkBack, KeyeventfKeyup)
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == (uint)inputs.Length;
    }

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