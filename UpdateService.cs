using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Desktype;

internal sealed record AvailableUpdate(string Version, string Sha256, string ExecutableUrl, string ChecksumsUrl);

internal static class UpdateService
{
    private const string RepositoryApi = "https://api.github.com/repos/Notagai/Desktype";
    private static readonly HttpClient Http = CreateHttpClient();

    public static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return $"{Math.Max(0, version.Major)}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}";
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Desktype-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<AvailableUpdate?> CheckAsync()
    {
        using var response = await Http.GetAsync($"{RepositoryApi}/releases/latest");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
            return null;

        var versionText = tag.TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out var latestVersion))
            throw new InvalidDataException($"The latest release has an invalid version tag: {tag}");

        var currentVersion = Version.Parse(CurrentVersion);
        if (latestVersion <= currentVersion) return null;

        string? executableUrl = null;
        string? checksumsUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == "Desktype.exe") executableUrl = url;
            else if (name == "SHA256SUMS.txt") checksumsUrl = url;
        }

        if (string.IsNullOrWhiteSpace(executableUrl) || string.IsNullOrWhiteSpace(checksumsUrl))
            throw new InvalidDataException("The latest release is missing Desktype.exe or SHA256SUMS.txt.");

        using var checksumResponse = await Http.GetAsync(checksumsUrl);
        checksumResponse.EnsureSuccessStatusCode();
        var checksumText = await checksumResponse.Content.ReadAsStringAsync();
        var match = Regex.Match(checksumText, @"(?im)^([a-f0-9]{64})\s+\*?Desktype\.exe\s*$");
        if (!match.Success)
            throw new InvalidDataException("Couldn't find the Desktype.exe SHA-256 checksum in the latest release.");

        return new AvailableUpdate(versionText, match.Groups[1].Value.ToLowerInvariant(),
            executableUrl, checksumsUrl);
    }

    public static async Task DownloadAndStartAsync(AvailableUpdate update, string executablePath, int processId)
    {
        var updateDirectory = Path.Combine(Path.GetTempPath(), "Desktype-update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateDirectory);
        var downloadedExe = Path.Combine(updateDirectory, "Desktype.exe");
        var scriptPath = Path.Combine(updateDirectory, "apply-update.ps1");

        try
        {
            using (var response = await Http.GetAsync(update.ExecutableUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var output = new FileStream(downloadedExe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(output);
            }

            await using (var input = File.OpenRead(downloadedExe))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(hash),
                    System.Text.Encoding.ASCII.GetBytes(update.Sha256)))
                    throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
            }

            const string script = @"
param(
    [Parameter(Mandatory=$true)][int]$TargetPid,
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination,
    [Parameter(Mandatory=$true)][string]$ExpectedSha256
)
$ErrorActionPreference = 'Stop'
$backup = ""$Destination.update-backup""
$log = Join-Path $env:TEMP 'Desktype-update.log'
function Write-UpdateLog([string]$Message) {
    Add-Content -LiteralPath $log -Value ""$(Get-Date -Format o) $Message""
}
try {
    Write-UpdateLog ""Update helper started for $Destination; waiting for process $TargetPid to exit.""
    while (Get-Process -Id $TargetPid -ErrorAction SilentlyContinue) {
        Start-Sleep -Milliseconds 250
    }
    Start-Sleep -Milliseconds 400
    if (Test-Path -LiteralPath $Destination) {
        Copy-Item -LiteralPath $Destination -Destination $backup -Force
    }
    Write-UpdateLog 'Installing verified executable.'
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    $actual = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedSha256.ToLowerInvariant()) {
        throw 'Installed executable checksum did not match.'
    }
    Start-Process -FilePath $Destination
    Write-UpdateLog 'Update installed and application restart requested successfully.'
    Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
} catch {
    $errorMessage = $_.Exception.ToString()
    try { Write-UpdateLog ""UPDATE FAILED: $errorMessage"" } catch {}
    if (Test-Path -LiteralPath $backup) {
        try {
            Copy-Item -LiteralPath $backup -Destination $Destination -Force
            Write-UpdateLog 'Restored previous executable from backup.'
        } catch {
            try { Write-UpdateLog ""RESTORE FAILED: $($_.Exception.ToString())"" } catch {}
        }
    }
    if (Test-Path -LiteralPath $Destination) {
        try { Start-Process -FilePath $Destination } catch {
            try { Write-UpdateLog ""RESTART FAILED: $($_.Exception.ToString())"" } catch {}
        }
    }
    try {
        Add-Type -AssemblyName PresentationFramework
        $details = ""Desktype could not finish installing the update. Your previous version was restored when possible."" +
            [Environment]::NewLine + [Environment]::NewLine + ""Details: $log"" +
            [Environment]::NewLine + [Environment]::NewLine + $errorMessage
        [System.Windows.MessageBox]::Show($details, 'Desktype update failed', 'OK', 'Warning') | Out-Null
    } catch {}
}
";
            await File.WriteAllTextAsync(scriptPath, script);
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-WindowStyle");
            start.ArgumentList.Add("Hidden");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(scriptPath);
            start.ArgumentList.Add("-TargetPid");
            start.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-Source");
            start.ArgumentList.Add(downloadedExe);
            start.ArgumentList.Add("-Destination");
            start.ArgumentList.Add(executablePath);
            start.ArgumentList.Add("-ExpectedSha256");
            start.ArgumentList.Add(update.Sha256);

            using var process = Process.Start(start);
            if (process is null) throw new InvalidOperationException("Couldn't start the update helper.");
        }
        catch
        {
            try { Directory.Delete(updateDirectory, recursive: true); } catch { }
            throw;
        }
    }
}
