using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VRCosme.Models;
using VRCosme.Services;

namespace VRCosme;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        LocalizationService.Initialize();
        ApplyTheme(ThemeService.GetThemeMode());

        base.OnStartup(e);

        // ログ初期化
        LogService.Initialize();

        // グローバル例外ハンドラ
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogService.Error("未処理の例外 (AppDomain)", ex);
        };

        DispatcherUnhandledException += (_, args) =>
        {
            LogService.Error("未処理の例外 (Dispatcher)", args.Exception);
            MessageBox.Show(
                LocalizationService.Format("Error.Unexpected",
                    "An unexpected error occurred.\n\n{0}", args.Exception.Message),
                LocalizationService.GetString("App.ErrorTitle", "VRCosme - Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogService.Error("未処理の例外 (Task)", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>テーマを適用</summary>
    public static void ApplyTheme(AppThemeMode mode)
    {
        // 現在のテーマリソースを削除
        var dictionariesToRemove = new List<ResourceDictionary>();
        foreach (var dict in Current.Resources.MergedDictionaries)
        {
            if (dict.Source?.ToString().Contains("Themes/") == true)
                dictionariesToRemove.Add(dict);
        }
        foreach (var dict in dictionariesToRemove)
            Current.Resources.MergedDictionaries.Remove(dict);

        // 新しいテーマを適用
        var effectiveTheme = mode == AppThemeMode.System
            ? ThemeService.GetEffectiveTheme()
            : mode;

        var themeUri = effectiveTheme == AppThemeMode.Dark
            ? new Uri("pack://application:,,,/Themes/DarkTheme.xaml")
            : new Uri("pack://application:,,,/Themes/LightTheme.xaml");

        Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = themeUri });
    }
}
