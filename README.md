# Desktype

A tiny Windows desktop utility that types supplied text into whichever text field currently has focus.

## Current MVP

- Simple WPF desktop UI
- Enter or edit the text to type
- Three-second countdown to switch to the target app
- Sends Unicode keyboard input to the focused field using Windows `SendInput`
- Stop button to cancel typing
- Small fixed delay between characters (temporary basic mode)

The human-like timing algorithm, configurable speed, typo/correction behavior, and global hotkeys are intentionally not implemented yet.

## Requirements

- Windows 10 or 11
- .NET 8 SDK

## Run from source

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Open PowerShell in the folder containing `Desktype.csproj`.
3. Run:

   ```powershell
   dotnet run
   ```

## Quick test

1. Open Desktype and leave the sample text in place.
2. Open Notepad and click inside its blank document.
3. Return to Desktype and click **Start typing**.
4. Within the three-second countdown, click back into Notepad.
5. The sample text should appear in Notepad. Try changing the text first, too.

Keep the test in a harmless text field first. The app types into whichever field has focus when the countdown ends; it does not verify the destination.

## Notes

- This first version targets Windows only.
- No backend, network connection, or external API is needed.
- Text is sent as Unicode keystrokes. Very large text and some special application fields may behave differently.
