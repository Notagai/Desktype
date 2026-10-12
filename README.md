# Desktype

A tiny Windows desktop utility that types supplied text into whichever text field currently has focus.

## Download for Windows

Open the [latest GitHub Release](https://github.com/Notagai/Desktype/releases/latest):

- **Desktype.exe** — portable, standalone Windows x64 download. No separate .NET installation is needed.
- **Desktype-Setup.exe** — installer with a Start Menu shortcut, optional desktop shortcut, and a Windows uninstaller.

The first public builds are unsigned, so Windows SmartScreen may show a warning. Download only from this repository's Releases page. The release includes **SHA256SUMS.txt** to help verify download integrity. See [distribution notes](DISTRIBUTION.md) for details and limitations.

## Current features

- WPF desktop interface
- Enter or edit the text to type
- Three-second countdown to switch to the target app
- Sends Unicode keyboard input to the focused field using Windows SendInput
- Pause/stop and resume typing
- In-memory typing progress and Reset Progress control
- Configurable typing settings and typo behavior as implemented in the app

Keep the test in a harmless text field first. The app types into whichever field has focus when the countdown ends; it does not verify the destination.

## Requirements

- Windows 10 or 11, 64-bit
- .NET 10 SDK only if running from source

## Run from source

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Open PowerShell in the folder containing Desktype.csproj.
3. Run:

   ```powershell
   dotnet run
   ```

## Build and release

The GitHub Actions workflow builds a self-contained Windows x64 EXE and Inno Setup installer. Push a tag such as v1.0.0 to create a GitHub Release with both downloads and SHA-256 checksums. See [DISTRIBUTION.md](DISTRIBUTION.md) for build details, installer behavior, signing considerations, and the pre-release checklist.

## Notes

- Windows only; x64 release builds.
- No backend, network connection, or external API is needed by the app itself.
- Text is sent as Unicode keystrokes. Very large text and some special application fields may behave differently.
