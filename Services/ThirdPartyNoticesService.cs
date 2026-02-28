using System.Diagnostics;
using System.IO;
using System.Windows;

namespace VRCosme.Services;

public static class ThirdPartyNoticesService
{
    private const string FileName = "THIRD-PARTY-NOTICES.txt";

    public static void Open(Window? owner = null)
    {
        var filePath = ResolveNoticesPath();
        if (filePath == null)
        {
            MessageBox.Show(
                owner,
                LocalizationService.Format(
                    "ThirdPartyNotices.NotFound",
                    "Could not find \"{0}\" in the app folder.",
                    FileName),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                owner,
                LocalizationService.Format(
                    "ThirdPartyNotices.OpenFailed",
                    "Failed to open \"{0}\".\n{1}",
                    FileName,
                    ex.Message),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static string? ResolveNoticesPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, FileName),
            Path.Combine(Environment.CurrentDirectory, FileName)
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir != null; i++)
        {
            var path = Path.Combine(dir.FullName, FileName);
            if (File.Exists(path))
                return path;
            dir = dir.Parent;
        }

        return null;
    }
}
