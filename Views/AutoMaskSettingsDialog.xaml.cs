using System.Windows;
using System.Windows.Controls;
using VRCosme.Models;
using VRCosme.Services;
using VRCosme.Services.AI;

namespace VRCosme.Views;

public partial class AutoMaskSettingsDialog : Window
{
    private sealed record SelectionModeOption(AutoMaskSelectionMode Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record SelectOption(AutoMaskTargetKind Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record DeviceOption(AutoMaskExecutionDevice Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ModelUi(TextBlock Name, TextBlock Status, Button Download, Button Delete);

    private readonly Dictionary<string, ModelUi> _modelUiById;
    private bool _isBusy;

    public AutoMaskSettingsDialog()
    {
        InitializeComponent();
        RefreshSamModelName();
        RefreshSamModelStatus();

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
        SelectionModeComboBox.ItemsSource = new[]
        {
            new SelectionModeOption(AutoMaskSelectionMode.Sam,
                LocalizationService.GetString(
                    "AutoMaskSettings.SelectionMode.Sam",
                    "SAM (Segment Anything, click-based)")),
            new SelectionModeOption(AutoMaskSelectionMode.SalientObjectDetection,
                LocalizationService.GetString(
                    "AutoMaskSettings.SelectionMode.SalientObjectDetection",
                    "Salient Object Detection (legacy)")),
        };
        SelectionModeComboBox.SelectedValuePath = nameof(SelectionModeOption.Value);
        SelectionModeComboBox.SelectedValue = ThemeService.GetAutoMaskSelectionMode();
        if (SelectionModeComboBox.SelectedIndex < 0)
            SelectionModeComboBox.SelectedIndex = 0;

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

        ExecutionDeviceComboBox.ItemsSource = new[]
        {
            new DeviceOption(AutoMaskExecutionDevice.Cpu,
                LocalizationService.GetString("AutoMaskSettings.ExecutionDevice.Cpu", "CPU")),
            new DeviceOption(AutoMaskExecutionDevice.Gpu,
                LocalizationService.GetString("AutoMaskSettings.ExecutionDevice.Gpu", "GPU")),
        };
        ExecutionDeviceComboBox.SelectedValuePath = nameof(DeviceOption.Value);
        ExecutionDeviceComboBox.SelectedValue = ThemeService.GetAutoMaskExecutionDevice();
        if (ExecutionDeviceComboBox.SelectedIndex < 0)
            ExecutionDeviceComboBox.SelectedIndex = 0;

        MultiPassCheckBox.IsChecked = ThemeService.GetAutoMaskMultiPassEnabled();
        UpdateSelectedModelText();
        UpdateSelectionModeDependentUi();
    }

    private void RefreshSamModelName()
    {
        var def = SamModelCatalog.GetDefault();
        var label = LocalizationService.GetString(def.DisplayNameKey, def.EncoderFileName);
        SamModelNameText.Text = label;
    }

    private void RefreshSamModelStatus()
    {
        var def = SamModelCatalog.GetDefault();
        bool installed = SamModelManager.IsModelInstalled(def);
        if (installed)
        {
            var (encBytes, decBytes) = SamModelManager.GetModelSizeBytes(def);
            double totalMb = (encBytes + decBytes) / (1024.0 * 1024.0);
            SamModelStatusText.Text = LocalizationService.Format(
                "AutoMaskSettings.Status.Installed", "Installed ({0:F1} MB)", totalMb);
        }
        else
        {
            SamModelStatusText.Text = LocalizationService.GetString(
                "AutoMaskSettings.Status.NotInstalled", "Not installed");
        }

        DownloadSamButton.IsEnabled = !_isBusy && !installed;
        DeleteSamButton.IsEnabled = !_isBusy && installed;
    }

    private async void DownloadSamModel_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var def = SamModelCatalog.GetDefault();
        SetBusy(true);
        try
        {
            await SamModelManager.EnsureModelDownloadedAsync(def);
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
            RefreshSamModelStatus();
        }
    }

    private void DeleteSamModel_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var def = SamModelCatalog.GetDefault();
        try
        {
            SamModelManager.DeleteModel(def);
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

        RefreshSamModelStatus();
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

    private void SelectionModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionModeDependentUi();
    }

    private void UpdateSelectionModeDependentUi()
    {
        bool isSamMode = SelectionModeComboBox.SelectedValue is AutoMaskSelectionMode.Sam;
        var legacyVisibility = isSamMode ? Visibility.Collapsed : Visibility.Visible;

        LegacyTargetRow.Visibility = legacyVisibility;
        LegacySelectedModelRow.Visibility = legacyVisibility;
        MultiPassCheckBox.Visibility = legacyVisibility;
        LegacyModelManagerSection.Visibility = legacyVisibility;
        SamSectionPanel.Visibility = isSamMode ? Visibility.Visible : Visibility.Collapsed;
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
        SelectionModeComboBox.IsEnabled = !value;
        TargetComboBox.IsEnabled = !value;
        ExecutionDeviceComboBox.IsEnabled = !value;
        MultiPassCheckBox.IsEnabled = !value;
        RefreshModelStatus();
        RefreshSamModelStatus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SelectionModeComboBox.SelectedValue is AutoMaskSelectionMode selectionMode)
            ThemeService.SaveAutoMaskSelectionMode(selectionMode);

        if (TargetComboBox.SelectedValue is AutoMaskTargetKind target)
            ThemeService.SaveAutoMaskTargetKind(target);

        if (ExecutionDeviceComboBox.SelectedValue is AutoMaskExecutionDevice device)
            ThemeService.SaveAutoMaskExecutionDevice(device);

        ThemeService.SaveAutoMaskMultiPassEnabled(MultiPassCheckBox.IsChecked == true);
        DialogResult = true;
    }
}
