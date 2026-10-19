using System.Windows;

namespace Desktype;

internal sealed record UpdateProgressInfo(
    string Status,
    long BytesReceived = 0,
    long? TotalBytes = null,
    bool IsIndeterminate = true);

public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow()
    {
        InitializeComponent();
    }

    public void ReportProgress(UpdateProgressInfo progress)
    {
        StatusLabel.Text = progress.Status;
        DownloadProgress.IsIndeterminate = progress.IsIndeterminate || progress.TotalBytes is not > 0;
        if (!DownloadProgress.IsIndeterminate)
        {
            DownloadProgress.Value = Math.Clamp(
                progress.BytesReceived * 100d / progress.TotalBytes!.Value, 0d, 100d);
            ProgressLabel.Text = $"{FormatBytes(progress.BytesReceived)} of {FormatBytes(progress.TotalBytes.Value)}";
        }
        else
        {
            DownloadProgress.Value = 0;
            ProgressLabel.Text = progress.Status.Contains("Verifying", StringComparison.OrdinalIgnoreCase)
                ? "Checking the downloaded file before installation…"
                : "Please keep this window open.";
        }
    }

    private static string FormatBytes(long bytes)
    {
        var mib = bytes / 1024d / 1024d;
        return mib >= 1 ? $"{mib:0.0} MB" : $"{bytes / 1024d:0} KB";
    }
}