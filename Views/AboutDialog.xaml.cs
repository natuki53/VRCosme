using System.Diagnostics;
using System.Reflection;
using System.Windows;
using VRCosme.Models;
using VRCosme.Services;

namespace VRCosme.Views;

public partial class AboutDialog : Window
{
    private const string XUrl = "https://x.com/mu_natuki";
    private const string GitHubUrl = "https://github.com/natuki53/VRCosme";

    public AboutDialog()
    {
        InitializeComponent();

        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? LocalizationService.GetString("About.VersionUnknown", "Unknown");

        VersionText.Text = LocalizationService.Format("About.VersionFormat", "Version {0}", version);

        ApplyThemeIcons();
    }

    private void ApplyThemeIcons()
    {
        var isDark = ThemeService.GetEffectiveTheme() == AppThemeMode.Dark;
        var darkVisible = isDark ? Visibility.Collapsed : Visibility.Visible;
        var lightVisible = isDark ? Visibility.Visible : Visibility.Collapsed;

        XIconDark.Visibility = darkVisible;
        XIconLight.Visibility = lightVisible;
        GitHubIconDark.Visibility = darkVisible;
        GitHubIconLight.Visibility = lightVisible;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void X_Link_Click(object sender, RoutedEventArgs e) => OpenUrl(XUrl);

    private void GitHub_Link_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubUrl);

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
