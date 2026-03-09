using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;

namespace VRCosme.Services.Update;

public static class UpdateDownloadService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public static string UpdatesDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VRCosme",
        "updates");

    public static async Task<string> DownloadInstallerAsync(
        string downloadUrl,
        string latestVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new ArgumentException("downloadUrl is empty.");

        Directory.CreateDirectory(UpdatesDirectory);

        var safeVersion = SanitizeFileName(latestVersion);
        var fileName = string.IsNullOrWhiteSpace(safeVersion)
            ? "VRCosme_Setup.exe"
            : $"VRCosme_Setup_{safeVersion}.exe";

        var targetPath = Path.Combine(UpdatesDirectory, fileName);
        var tempPath = targetPath + ".download";

        try
        {
            using var response = await Client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            if (File.Exists(targetPath))
                File.Delete(targetPath);

            File.Move(tempPath, targetPath);
            return targetPath;
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // ignore cleanup error
            }

            throw;
        }
    }

    public static void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true
        });
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
