using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using VRCosme.Models;
using VRCosme.Services;
using VRCosme.Services.AI;
using VRCosme.Services.ImageProcessing;

namespace VRCosme.ViewModels;

public partial class MainViewModel
{
    // ───────── コマンド: ファイル操作 ─────────

    [RelayCommand]
    private async Task OpenImageAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = LocalizationService.GetString("Dialog.OpenImage.Filter",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif;*.webp|All Files|*.*"),
            Title = LocalizationService.GetString("Dialog.OpenImage.Title", "Open Image")
        };
        if (dialog.ShowDialog() == true) await LoadImageAsync(dialog.FileName);
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string? filePath)
    {
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            await LoadImageAsync(filePath);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_transformedImage == null) return;
        var isPng = SelectedExportFormat == "PNG";
        var ext = isPng ? "png" : "jpg";
        var filter = isPng
            ? LocalizationService.GetString("Dialog.Export.FilterPng", "PNG Files|*.png")
            : LocalizationService.GetString("Dialog.Export.FilterJpeg", "JPEG Files|*.jpg;*.jpeg");

        var defaultDir = ThemeService.GetDefaultExportDirectory();
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            DefaultExt = ext,
            FileName = Path.GetFileNameWithoutExtension(SourceFilePath ?? "image") + $"_edited.{ext}",
            Title = LocalizationService.GetString("Dialog.Export.Title", "Export Image")
        };
        if (!string.IsNullOrEmpty(defaultDir) && Directory.Exists(defaultDir))
            dialog.InitialDirectory = defaultDir;
        if (dialog.ShowDialog() != true) return;

        IsProcessing = true;
        StatusMessage = LocalizationService.GetString("Status.Exporting", "Exporting...");
        LogService.Info($"書き出し開始: {dialog.FileName} (形式={SelectedExportFormat}, クロップ={IsCropActive})");
        try
        {
            var p = BuildParams();
            var cropActive = IsCropActive;
            var cropRect = new SixLabors.ImageSharp.Rectangle(
                (int)CropX, (int)CropY, (int)CropWidth, (int)CropHeight);
            var path = dialog.FileName;
            var format = isPng ? ExportFormat.Png : ExportFormat.Jpeg;
            var quality = JpegQuality;
            var original = _transformedImage.Clone();
            var layerAdjustmentSequence = BuildMaskAdjustmentSequence(
                original.Width,
                original.Height,
                requireMaskEnabled: true);

            await Task.Run(() =>
            {
                SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>? current = null;
                try
                {
                    current = ImageProcessor.ApplyAdjustments(original, p);
                    foreach (var (mask, layerParams, naturalizeBoundary) in layerAdjustmentSequence)
                    {
                        var next = ImageProcessor.ApplyAdjustments(current, layerParams, mask, naturalizeBoundary);
                        current.Dispose();
                        current = next;
                    }

                    if (cropActive)
                    {
                        using var cropped = ImageProcessor.Crop(current, cropRect);
                        ImageProcessor.Export(cropped, path, format, quality);
                    }
                    else
                    {
                        ImageProcessor.Export(current, path, format, quality);
                    }
                }
                finally
                {
                    current?.Dispose();
                    original.Dispose();
                }
            });

            LogService.Info($"書き出し完了: {path}");
            StatusMessage = LocalizationService.GetString("Status.ExportComplete", "Export complete");
            MessageBox.Show(LocalizationService.GetString("Dialog.Export.Success", "Export completed successfully."),
                LocalizationService.GetString("App.Name", "VRCosme"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogService.Error($"書き出し失敗: {dialog.FileName}", ex);
            MessageBox.Show(
                LocalizationService.Format("Dialog.Export.Failed", "Failed to export image:\n{0}", ex.Message),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusMessage = LocalizationService.GetString("Status.ExportError", "Export error");
        }
        finally { IsProcessing = false; }
    }

    [RelayCommand]
    private async Task AutoSelectMaskAsync()
    {
        if (!HasImage || ImageWidth <= 0 || ImageHeight <= 0) return;

        double centerX = (ImageWidth - 1) * 0.5;
        double centerY = (ImageHeight - 1) * 0.5;
        await AutoSelectMaskAtAsync(centerX, centerY);
    }

    public async Task<bool> AutoSelectMaskAtAsync(double imageX, double imageY)
    {
        if (!HasImage || _transformedImage == null || ImageWidth <= 0 || ImageHeight <= 0)
            return false;
        if (IsProcessing)
            return false;

        var targetKind = ThemeService.GetAutoMaskTargetKind();
        var model = AutoMaskModelCatalog.GetForTarget(targetKind);
        var modelPath = await EnsureAutoMaskModelPathAsync(model);
        if (string.IsNullOrWhiteSpace(modelPath))
            return false;

        bool useMultiPass = ThemeService.GetAutoMaskMultiPassEnabled();
        int seedX = Math.Clamp((int)Math.Round(imageX), 0, ImageWidth - 1);
        int seedY = Math.Clamp((int)Math.Round(imageY), 0, ImageHeight - 1);

        IsProcessing = true;
        StatusMessage = LocalizationService.GetString("Status.AutoSelectingMask", "Selecting mask with AI...");

        try
        {
            if (SelectedMaskLayer is null)
                AddMaskLayerInternal(pushUndoSnapshot: false);

            var input = _transformedImage.Clone();
            var result = await Task.Run(() =>
            {
                try
                {
                    return _autoMaskSelector.CreateMask(input, modelPath, seedX, seedY, useMultiPass);
                }
                finally
                {
                    input.Dispose();
                }
            });

            bool changed = MergeSelectedMaskFromBinary(result.Mask, result.Width, result.Height);
            StatusMessage = BuildReadyStatusMessage();
            return changed;
        }
        catch (Exception ex)
        {
            LogService.Error($"AI自動選択に失敗: x={seedX}, y={seedY}", ex);
            MessageBox.Show(
                LocalizationService.Format("Dialog.AIAutoMask.Failed", "Failed to select mask with AI:\n{0}", ex.Message),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusMessage = LocalizationService.GetString("Status.AIAutoMaskError", "AI auto mask error");
            return false;
        }
        finally
        {
            IsProcessing = false;
        }
    }

    // ───────── コマンド: 補正リセット ─────────

    private void ResetBasicAdjustments()
    {
        Brightness = 0;
        Contrast = 0;
        Gamma = 1.0;
        Exposure = 0;
        Saturation = 0;
        Temperature = 0;
        Tint = 0;
    }

    private void ResetDetailedAdjustmentValues()
    {
        Shadows = 0;
        Highlights = 0;
        Clarity = 0;
        Blur = 0;
        Sharpen = 0;
        Vignette = 0;
    }

    private void ResetAllAdjustments()
    {
        ResetBasicAdjustments();
        ResetDetailedAdjustmentValues();
    }

    [RelayCommand]
    private void ResetAdjustments()
    {
        LogService.Info("基本補正リセット");
        PushUndoSnapshot();
        ResetBasicAdjustments();
    }

    [RelayCommand]
    private void ResetDetailedAdjustments()
    {
        LogService.Info("詳細補正リセット");
        PushUndoSnapshot();
        ResetDetailedAdjustmentValues();
    }

    [RelayCommand]
    private void ResetParameter(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        PushUndoSnapshot();
        switch (name)
        {
            case nameof(Brightness): Brightness = 0; break;
            case nameof(Contrast): Contrast = 0; break;
            case nameof(Gamma): Gamma = 1.0; break;
            case nameof(Exposure): Exposure = 0; break;
            case nameof(Saturation): Saturation = 0; break;
            case nameof(Temperature): Temperature = 0; break;
            case nameof(Tint): Tint = 0; break;
            case nameof(Shadows): Shadows = 0; break;
            case nameof(Highlights): Highlights = 0; break;
            case nameof(Clarity): Clarity = 0; break;
            case nameof(Blur): Blur = 0; break;
            case nameof(Sharpen): Sharpen = 0; break;
            case nameof(Vignette): Vignette = 0; break;
        }
    }

    [RelayCommand]
    private void ResetCrop()
    {
        PushUndoSnapshot();
        SelectedCropRatio = CropRatios[0];
    }

    [RelayCommand]
    private async Task ResetAllAsync()
    {
        LogService.Info("すべてリセット");
        PushUndoSnapshot();
        ResetAllAdjustments();
        SelectedCropRatio = CropRatios[0];
        IsCropActive = false;
        ResetMaskForNewImage();
        _rotationDegrees = 0;
        _flipHorizontal = false;
        _flipVertical = false;
        if (_pristineImage != null) await ApplyTransformAsync();
    }

    // ───────── コマンド: プリセット ─────────

    [RelayCommand]
    private void ApplyPreset(PresetItem? preset)
    {
        if (preset == null) return;
        LogService.Info($"プリセット適用: {preset.Name}");
        PushUndoSnapshot();
        Brightness = preset.Brightness;
        Contrast = preset.Contrast;
        Gamma = preset.Gamma;
        Exposure = preset.Exposure;
        Saturation = preset.Saturation;
        Temperature = preset.Temperature;
        Tint = preset.Tint;
        Shadows = preset.Shadows;
        Highlights = preset.Highlights;
        Clarity = preset.Clarity;
        Blur = preset.Blur;
        Sharpen = preset.Sharpen;
        Vignette = preset.Vignette;
    }

    // ───────── コマンド: 表示 ─────────

    [RelayCommand]
    private void SetCompareMode(string? mode)
    {
        CompareMode = mode switch
        {
            "Before" => CompareMode.Before,
            "Split" => CompareMode.Split,
            _ => CompareMode.After,
        };
    }

    [RelayCommand]
    private void SetFitToScreen() { IsFitToScreen = true; }

    [RelayCommand]
    private void SetZoom100() { IsFitToScreen = false; }

    private async Task<string?> EnsureAutoMaskModelPathAsync(AutoMaskModelDefinition model)
    {
        var modelPath = AutoMaskModelManager.GetModelPath(model);
        if (AutoMaskModelManager.IsModelInstalled(model))
            return modelPath;

        IsProcessing = true;
        StatusMessage = LocalizationService.GetString("Status.DownloadingAutoMaskModel", "Downloading AI mask model...");

        try
        {
            return await AutoMaskModelManager.EnsureModelDownloadedAsync(model);
        }
        catch (Exception ex)
        {
            LogService.Error("AI自動選択モデルのダウンロードに失敗", ex);
            MessageBox.Show(
                LocalizationService.Format(
                    "Dialog.AIAutoMask.DownloadFailed",
                    "Failed to download AI auto mask model:\n{0}",
                    ex.Message),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusMessage = LocalizationService.GetString("Status.AIAutoMaskError", "AI auto mask error");
            return null;
        }
        finally
        {
            IsProcessing = false;
        }
    }
}
