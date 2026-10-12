# Desktype distribution notes

## What gets published

Pushing a version tag such as `v1.0.0` triggers the Windows packaging workflow. A successful run publishes these files to the GitHub Release:

- **`Desktype.exe`** — portable, self-contained Windows x64 executable. It includes the .NET runtime and does not require a separate .NET installation.
- **`Desktype-Setup.exe`** — per-user installer for Windows x64. It creates a Start Menu shortcut, optionally creates a desktop shortcut, and registers a normal Windows uninstaller. It installs under the current user's Local AppData and should not require administrator rights.
- **`SHA256SUMS.txt`** — SHA-256 hashes for both downloads.

The workflow also uploads these files as a temporary GitHub Actions artifact for builds from branches and pull requests. Release assets are created only for version tags beginning with `v`.

## Build locally

On Windows with the .NET 10 SDK and Inno Setup 6 installed:

```powershell
dotnet publish Desktype.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/publish
New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item dist/publish/Desktype.exe dist/Desktype.exe
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DMyAppVersion=1.0.0 installer/Desktype.iss
Get-FileHash dist/Desktype.exe, dist/Desktype-Setup.exe -Algorithm SHA256
```

## Download and verification

Download the executable or installer from the **Releases** section of this repository, rather than from third-party mirrors. To check a downloaded file in PowerShell, compare its hash with the matching entry in `SHA256SUMS.txt`:

```powershell
Get-FileHash .\Desktype-Setup.exe -Algorithm SHA256
```

A matching hash helps detect accidental or malicious changes in transit; it does not by itself prove who built or signed a file.

## Signing and Windows warnings

The workflow currently builds **unsigned** binaries. Windows SmartScreen may warn users about downloads from an unfamiliar publisher, and some managed devices may block unsigned software. Do not describe the downloads as signed or Microsoft-verified.

For broader public distribution, consider a trusted code-signing service and sign the application executable and installer in a protected release workflow. Keep signing credentials in protected GitHub Actions secrets or an external signing service; never commit certificates, private keys, or tokens. Signing alone does not guarantee that SmartScreen warnings disappear immediately.

## Compliance and maintenance checklist

This automation is a packaging setup, not a legal certification. Before distributing publicly:

- Choose and publish a software license only if you have the rights to grant it; do not assume that a public GitHub repository automatically grants reuse rights.
- Review third-party dependency and .NET runtime licensing/notice requirements for the exact release.
- Keep a privacy statement accurate. The app documentation says it has no backend, network connection, or external API; re-check that claim if features change.
- Test installation, launch, upgrade-over-previous-version, and uninstall on a clean Windows 10/11 x64 machine before calling a release stable.
- Test the portable EXE on a clean machine without the .NET runtime installed.
- Review accessibility, security, and any consumer-protection or distribution requirements applicable to your jurisdiction and audience.
- Keep .NET servicing current and rebuild/release when security updates are needed.

The installer is per-user and its uninstaller removes the installed application and shortcuts. It does not intentionally delete user-created files outside the installation directory.
