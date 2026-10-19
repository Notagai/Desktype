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

## In-app updates

Desktype checks the latest published, non-prerelease GitHub Release at startup and offers to update when a newer version is available. Users can also select **Check updates** in the app. The updater downloads `Desktype.exe`, verifies its SHA-256 against `SHA256SUMS.txt`, while a popup displays download progress and verification status. Downloads have a 250 MiB size limit and a three-minute deadline. It then launches a small PowerShell helper that waits up to two minutes for Desktype to exit, creates and verifies a unique rollback backup, replaces the running executable, verifies the installed file, and restarts it. If installation fails, the helper restores and verifies the backup when possible. This supports both the portable executable and the per-user installed executable without requiring administrator rights. The app does not install an update without the user's confirmation. The input box is limited to 50,000 characters. CRLF and lone-CR line endings are normalized to LF before typing, and status progress updates are throttled for large inputs.

Update checks require an internet connection to the public GitHub Releases API. If GitHub is unavailable, the current app remains usable.

## Download and verification

Download the executable or installer from the **Releases** section of this repository, rather than from third-party mirrors. To check a downloaded file in PowerShell, compare its hash with the matching entry in `SHA256SUMS.txt`:

```powershell
Get-FileHash .\Desktype-Setup.exe -Algorithm SHA256
```

A matching hash helps detect accidental or malicious changes in transit; it does not by itself prove who built or signed a file.

## Signing and Windows warnings

Code signing is enabled when a certificate is available. Configure these repository Actions secrets before creating a signed version tag release; if they are absent, the workflow warns and publishes the release unsigned:

- **`WINDOWS_SIGNING_CERTIFICATE_PFX`** — the base64-encoded contents of a valid code-signing PFX certificate.
- **`WINDOWS_SIGNING_CERTIFICATE_PASSWORD`** — the PFX password.

When configured, the workflow signs both `Desktype.exe` and `Desktype-Setup.exe` with SHA-256, uses a trusted RFC 3161 timestamp service, verifies both signatures, then generates the published SHA-256 checksums. If signing secrets are present but signing or verification fails, the release fails rather than publishing binaries with a broken signature. Branch builds and pull-request builds do not need signing secrets.

Obtain the certificate from a trusted code-signing provider. Never commit a certificate, private key, password, or token to the repository. Existing releases remain as originally published and may be unsigned. Authenticode signing helps establish publisher identity and file integrity, but does not guarantee that SmartScreen warnings disappear immediately.

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
