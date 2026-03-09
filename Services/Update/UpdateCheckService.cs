using System.Windows;
using VRCosme.Models;
using VRCosme.Services;
using VRCosme.Views;

namespace VRCosme.Services.Update;

public static class UpdateCheckService
{
    private const string SetupAssetName = "VRCosme_Setup.exe";

    public static async Task CheckForUpdatesOnStartupAsync(Window owner)
    {
        if (!ThemeService.GetAutoUpdateEnabled() || !ThemeService.GetAutoUpdateCheckOnStartup())
            return;

        await CheckForUpdatesAsync(owner);
    }

    private static async Task CheckForUpdatesAsync(Window owner)
    {
        LogService.Info("更新チェック開始");
        ThemeService.SaveLastUpdateCheckUtc(DateTime.UtcNow);

        var release = await GitHubReleaseClient.GetLatestReleaseAsync();
        if (release == null)
            return;

        if (release.Draft || release.Prerelease)
        {
            LogService.Info("更新チェック: 最新リリースが draft/prerelease のためスキップ");
            return;
        }

        var currentRaw = VersionComparer.GetCurrentVersionRaw();
        if (!VersionComparer.TryCompare(currentRaw, release.TagName, out var comparison, out var currentVersion, out var latestVersion))
        {
            LogService.Info("更新チェック: バージョン比較に失敗");
            return;
        }

        if (comparison >= 0)
        {
            LogService.Info("更新チェック: 最新版なし");
            return;
        }

        var skipped = ThemeService.GetSkippedUpdateVersion();
        if (!string.IsNullOrWhiteSpace(skipped)
            && string.Equals(skipped, latestVersion, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Info($"更新チェック: スキップ済みのため通知省略 ({latestVersion})");
            return;
        }

        var asset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, SetupAssetName, StringComparison.OrdinalIgnoreCase));
        var downloadUrl = asset?.BrowserDownloadUrl;
        if (asset == null)
            LogService.Info("更新チェック: インストーラー asset が見つかりません");

        LogService.Info($"更新チェック: 更新あり {currentVersion} -> {latestVersion}");

        var info = new UpdateInfo(
            currentVersion,
            latestVersion,
            release.Body,
            release.HtmlUrl,
            downloadUrl);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new UpdateDialog(info) { Owner = owner };
            dialog.ShowDialog();
        });
    }
}
