using System.Windows;
using VRCosme.Models;
using VRCosme.Services;
using VRCosme.ViewModels;

namespace VRCosme.Views;

public partial class SettingsDialog : Window
{
    private static readonly AppThemeMode[] ThemeModes = [AppThemeMode.System, AppThemeMode.Light, AppThemeMode.Dark];
    private readonly string _currentLanguageCode;

    private sealed record SelectOption(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    public SettingsDialog()
    {
        InitializeComponent();
        _currentLanguageCode = ThemeService.GetLanguage();
        LoadCurrentSettings();
        JpegQualitySlider.ValueChanged += (_, _) =>
            JpegQualityLabel.Text = ((int)JpegQualitySlider.Value).ToString();
    }

    private void LoadCurrentSettings()
    {
        // テーマ
        ThemeComboBox.ItemsSource = new[]
        {
            LocalizationService.GetString("Theme.System", "System"),
            LocalizationService.GetString("Theme.Light", "Light"),
            LocalizationService.GetString("Theme.Dark", "Dark"),
        };
        var currentTheme = ThemeService.GetThemeMode();
        ThemeComboBox.SelectedIndex = Array.IndexOf(ThemeModes, currentTheme);
        if (ThemeComboBox.SelectedIndex < 0) ThemeComboBox.SelectedIndex = 0;

        // 言語
        LanguageComboBox.ItemsSource = new[]
        {
            new SelectOption(ThemeService.JapaneseLanguageCode,
                LocalizationService.GetString("Language.Option.Japanese", "Japanese")),
            new SelectOption(ThemeService.EnglishLanguageCode,
                LocalizationService.GetString("Language.Option.English", "English")),
            new SelectOption(ThemeService.KoreanLanguageCode,
                LocalizationService.GetString("Language.Option.Korean", "Korean")),
            new SelectOption(ThemeService.ChineseSimplifiedLanguageCode,
                LocalizationService.GetString("Language.Option.ChineseSimplified", "Chinese (Simplified)")),
            new SelectOption(ThemeService.ChineseTraditionalLanguageCode,
                LocalizationService.GetString("Language.Option.ChineseTraditional", "Chinese (Traditional)")),
        };
        LanguageComboBox.SelectedValuePath = nameof(SelectOption.Value);
        LanguageComboBox.SelectedValue = _currentLanguageCode;

        // 書き出しディレクトリ
        ExportDirTextBox.Text = ThemeService.GetDefaultExportDirectory();

        // 書き出し形式
        ExportFormatComboBox.ItemsSource = new[] { "PNG", "JPEG" };
        ExportFormatComboBox.SelectedItem = ThemeService.GetDefaultExportFormat();

        // JPEG品質
        JpegQualitySlider.Value = ThemeService.GetDefaultJpegQuality();
        JpegQualityLabel.Text = ThemeService.GetDefaultJpegQuality().ToString();

        // グリッド線
        GridCheckBox.IsChecked = ThemeService.GetShowRuleOfThirdsGrid();

        // ルーラー
        RulerCheckBox.IsChecked = ThemeService.GetShowRuler();
    }

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = LocalizationService.GetString("Dialog.SelectExportDirectory.Title",
                "Select default export directory")
        };
        var currentDir = ExportDirTextBox.Text;
        if (!string.IsNullOrEmpty(currentDir) && System.IO.Directory.Exists(currentDir))
            dialog.InitialDirectory = currentDir;

        if (dialog.ShowDialog() == true)
            ExportDirTextBox.Text = dialog.FolderName;
    }

    private void OpenAutoMaskSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AutoMaskSettingsDialog { Owner = this };
        dialog.ShowDialog();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        var selectedLanguage = LocalizationService.NormalizeLanguageCode(
            LanguageComboBox.SelectedValue as string ?? _currentLanguageCode);

        // テーマ
        var themeIndex = ThemeComboBox.SelectedIndex;
        if (themeIndex >= 0 && themeIndex < ThemeModes.Length)
        {
            var mode = ThemeModes[themeIndex];
            ThemeService.SaveThemeMode(mode);
            App.ApplyTheme(mode);
        }

        // 書き出しディレクトリ
        ThemeService.SaveDefaultExportDirectory(ExportDirTextBox.Text.Trim());

        // 書き出し形式
        var format = ExportFormatComboBox.SelectedItem as string ?? "PNG";
        ThemeService.SaveDefaultExportFormat(format);

        // JPEG品質
        ThemeService.SaveDefaultJpegQuality((int)JpegQualitySlider.Value);

        // グリッド線
        ThemeService.SaveShowRuleOfThirdsGrid(GridCheckBox.IsChecked == true);

        // ルーラー
        ThemeService.SaveShowRuler(RulerCheckBox.IsChecked == true);

        if (!string.Equals(selectedLanguage, _currentLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            LocalizationService.SetLanguage(selectedLanguage);
            if (Owner is MainWindow ownerWindow && ownerWindow.DataContext is MainViewModel vm)
                vm.RefreshLocalization();
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
