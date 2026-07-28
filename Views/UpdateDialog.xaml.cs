using System.Diagnostics;
using System.Windows;
using VRCosme.Models;
using VRCosme.Services;
using VRCosme.Services.Update;

namespace VRCosme.Views;

public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _info;
    private bool _downloading;

    public UpdateDialog(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;

        CurrentVersionText.Text = info.CurrentVersion;
        LatestVersionText.Text = info.LatestVersion;

        ReleaseNotesTextBox.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? LocalizationService.GetString("Update.ReleaseNotesEmpty", "No release notes.")
            : info.ReleaseNotes;

        if (!info.HasDownload)
        {
            DownloadButton.IsEnabled = false;
            StatusText.Text = LocalizationService.GetString("Update.NoDownload", "Download is not available.");
            StatusText.Visibility = Visibility.Visible;
        }

        OpenReleaseButton.IsEnabled = !string.IsNullOrWhiteSpace(info.ReleasePageUrl);
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading || !_info.HasDownload || _info.DownloadUrl == null)
            return;

        SetDownloadingState(true);
        try
        {
            var installerPath = await UpdateDownloadService.DownloadInstallerAsync(
                _info.DownloadUrl, _info.LatestVersion);

            SetDownloadingState(false);

            var confirm = MessageBox.Show(
                LocalizationService.GetString("Update.InstallConfirm",
                    "Start the installer now? VRCosme Classic will close after launching."),
                LocalizationService.GetString("Update.InstallTitle", "Update"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm == MessageBoxResult.Yes)
            {
                UpdateDownloadService.LaunchInstaller(installerPath);
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            SetDownloadingState(false);
            MessageBox.Show(
                LocalizationService.Format("Update.DownloadFailed",
                    "Download failed:\n{0}", ex.Message),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.SaveSkippedUpdateVersion(_info.LatestVersion);
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_info.ReleasePageUrl))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = _info.ReleasePageUrl,
            UseShellExecute = true
        });
    }

    private void SetDownloadingState(bool downloading)
    {
        _downloading = downloading;
        DownloadProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = downloading
            ? LocalizationService.GetString("Update.Status.Downloading", "Downloading...")
            : "";
        StatusText.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;

        DownloadButton.IsEnabled = !downloading && _info.HasDownload;
        SkipButton.IsEnabled = !downloading;
        LaterButton.IsEnabled = !downloading;
        OpenReleaseButton.IsEnabled = !downloading;
    }
}
