using System.Windows;
using System.Windows.Controls;
using VRCosme.Models;
using VRCosme.Services;
using VRCosme.Services.AI;

namespace VRCosme.Views;

public partial class AutoMaskSettingsDialog : Window
{
    private sealed record SelectOption(AutoMaskTargetKind Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ModelUi(TextBlock Name, TextBlock Status, Button Download, Button Delete);

    private readonly Dictionary<string, ModelUi> _modelUiById;
    private bool _isBusy;

    public AutoMaskSettingsDialog()
    {
        InitializeComponent();

        _modelUiById = new Dictionary<string, ModelUi>(StringComparer.OrdinalIgnoreCase)
        {
            ["u2net_human_seg"] = new(ModelNameHumanText, ModelStatusHumanText, DownloadHumanButton, DeleteHumanButton),
            ["isnet_general_use"] = new(ModelNameObjectText, ModelStatusObjectText, DownloadObjectButton, DeleteObjectButton),
            ["u2net"] = new(ModelNameBackgroundText, ModelStatusBackgroundText, DownloadBackgroundButton, DeleteBackgroundButton),
            ["isnet_anime"] = new(ModelNameOtherText, ModelStatusOtherText, DownloadOtherButton, DeleteOtherButton),
            ["silueta"] = new(ModelNameLightweightText, ModelStatusLightweightText, DownloadLightweightButton, DeleteLightweightButton),
        };

        LoadCurrentSettings();
        RefreshModelNames();
        RefreshModelStatus();
    }

    private void LoadCurrentSettings()
    {
        TargetComboBox.ItemsSource = new[]
        {
            new SelectOption(AutoMaskTargetKind.Human,
                LocalizationService.GetString("AutoMaskSettings.Target.Human", "Human")),
            new SelectOption(AutoMaskTargetKind.Object,
                LocalizationService.GetString("AutoMaskSettings.Target.Object", "Object")),
            new SelectOption(AutoMaskTargetKind.Background,
                LocalizationService.GetString("AutoMaskSettings.Target.Background", "Background")),
            new SelectOption(AutoMaskTargetKind.Other,
                LocalizationService.GetString("AutoMaskSettings.Target.Other", "Other")),
            new SelectOption(AutoMaskTargetKind.Lightweight,
                LocalizationService.GetString("AutoMaskSettings.Target.Lightweight", "Lightweight")),
        };
        TargetComboBox.SelectedValuePath = nameof(SelectOption.Value);
        TargetComboBox.SelectedValue = ThemeService.GetAutoMaskTargetKind();
        if (TargetComboBox.SelectedIndex < 0)
            TargetComboBox.SelectedIndex = 0;

        MultiPassCheckBox.IsChecked = ThemeService.GetAutoMaskMultiPassEnabled();
        UpdateSelectedModelText();
    }

    private void RefreshModelNames()
    {
        foreach (var definition in AutoMaskModelCatalog.GetAll())
        {
            if (!_modelUiById.TryGetValue(definition.Id, out var ui))
                continue;

            var label = LocalizationService.GetString(definition.DisplayNameKey, definition.FileName);
            ui.Name.Text = $"{label} ({definition.FileName})";
        }
    }

    private void RefreshModelStatus()
    {
        foreach (var definition in AutoMaskModelCatalog.GetAll())
        {
            if (!_modelUiById.TryGetValue(definition.Id, out var ui))
                continue;

            bool installed = AutoMaskModelManager.IsModelInstalled(definition);
            long sizeBytes = AutoMaskModelManager.GetModelSizeBytes(definition);
            if (installed)
            {
                var sizeMb = sizeBytes / (1024.0 * 1024.0);
                ui.Status.Text = LocalizationService.Format(
                    "AutoMaskSettings.Status.Installed",
                    "Installed ({0:F1} MB)",
                    sizeMb);
            }
            else
            {
                ui.Status.Text = LocalizationService.GetString(
                    "AutoMaskSettings.Status.NotInstalled",
                    "Not installed");
            }

            ui.Download.IsEnabled = !_isBusy && !installed;
            ui.Delete.IsEnabled = !_isBusy && installed;
        }
    }

    private void TargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedModelText();
    }

    private void UpdateSelectedModelText()
    {
        if (TargetComboBox.SelectedValue is not AutoMaskTargetKind target)
            return;

        var definition = AutoMaskModelCatalog.GetForTarget(target);
        var modelName = LocalizationService.GetString(definition.DisplayNameKey, definition.FileName);
        var status = AutoMaskModelManager.IsModelInstalled(definition)
            ? LocalizationService.GetString("AutoMaskSettings.Status.InstalledShort", "installed")
            : LocalizationService.GetString("AutoMaskSettings.Status.NotInstalledShort", "not installed");
        SelectedModelText.Text = $"{modelName} ({definition.FileName}, {status})";
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (sender is not FrameworkElement { Tag: string modelId }) return;
        if (!AutoMaskModelCatalog.TryGetById(modelId, out var definition)) return;

        SetBusy(true);
        try
        {
            await AutoMaskModelManager.EnsureModelDownloadedAsync(definition);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                LocalizationService.Format(
                    "Dialog.AIAutoMask.DownloadFailed",
                    "Failed to download AI auto mask model:\n{0}",
                    ex.Message),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshModelStatus();
            UpdateSelectedModelText();
        }
    }

    private void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (sender is not FrameworkElement { Tag: string modelId }) return;
        if (!AutoMaskModelCatalog.TryGetById(modelId, out var definition)) return;

        try
        {
            AutoMaskModelManager.DeleteModel(definition);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                LocalizationService.Format(
                    "Dialog.AIAutoMask.DeleteFailed",
                    "Failed to delete AI auto mask model:\n{0}",
                    ex.Message),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        RefreshModelStatus();
        UpdateSelectedModelText();
    }

    private void SetBusy(bool value)
    {
        _isBusy = value;
        Cursor = value ? System.Windows.Input.Cursors.Wait : null;
        TargetComboBox.IsEnabled = !value;
        MultiPassCheckBox.IsEnabled = !value;
        RefreshModelStatus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (TargetComboBox.SelectedValue is AutoMaskTargetKind target)
            ThemeService.SaveAutoMaskTargetKind(target);

        ThemeService.SaveAutoMaskMultiPassEnabled(MultiPassCheckBox.IsChecked == true);
        DialogResult = true;
    }
}
