namespace VRCosme.Models;

public sealed class UpdateInfo
{
    public UpdateInfo(
        string currentVersion,
        string latestVersion,
        string releaseNotes,
        string releasePageUrl,
        string? downloadUrl)
    {
        CurrentVersion = currentVersion;
        LatestVersion = latestVersion;
        ReleaseNotes = releaseNotes;
        ReleasePageUrl = releasePageUrl;
        DownloadUrl = downloadUrl;
    }

    public string CurrentVersion { get; }
    public string LatestVersion { get; }
    public string ReleaseNotes { get; }
    public string ReleasePageUrl { get; }
    public string? DownloadUrl { get; }

    public bool HasDownload => !string.IsNullOrWhiteSpace(DownloadUrl);
}
