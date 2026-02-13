using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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

    /// <summary>マスク自動選択の共通フレームワーク。ガード・IsProcessing・レイヤー確保・Undo を共通化。</summary>
    private async Task<bool> RunMaskSelectionAsync(
        string statusKey,
        string statusDefault,
        string logPrefix,
        string errorDialogKey,
        string errorDialogDefault,
        string errorStatusKey,
        string errorStatusDefault,
        Func<Image<Rgba32>, (byte[] Mask, int Width, int Height)?> buildMask,
        Func<byte[], int, int, bool> applyMask)
    {
        if (!HasImage || _transformedImage == null || ImageWidth <= 0 || ImageHeight <= 0)
            return false;
        if (IsProcessing)
            return false;

        IsProcessing = true;
        StatusMessage = LocalizationService.GetString(statusKey, statusDefault);

        try
        {
            EditState? snapshotBeforeOperation = null;
            bool createdMaskLayer = false;
            if (SelectedMaskLayer is null)
            {
                snapshotBeforeOperation = CreateSnapshot();
                AddMaskLayerInternal(pushUndoSnapshot: false);
                createdMaskLayer = true;
            }

            var input = _transformedImage.Clone();
            var result = await Task.Run(() => buildMask(input));
            if (result is null)
                return false;

            var (mask, width, height) = result.Value;
            bool changed = applyMask(mask, width, height);
            if (changed && createdMaskLayer && snapshotBeforeOperation is not null)
                ReplaceLatestUndoState(snapshotBeforeOperation);
            StatusMessage = BuildReadyStatusMessage();
            return changed;
        }
        catch (Exception ex)
        {
            LogService.Error($"{logPrefix}", ex);
            MessageBox.Show(
                LocalizationService.Format(errorDialogKey, errorDialogDefault, ex.Message),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusMessage = LocalizationService.GetString(errorStatusKey, errorStatusDefault);
            return false;
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task AutoSelectMaskAsync()
    {
        var targetKind = ThemeService.GetAutoMaskTargetKind();
        var model = AutoMaskModelCatalog.GetForTarget(targetKind);
        var modelPath = await EnsureAutoMaskModelPathAsync(model);
        if (string.IsNullOrWhiteSpace(modelPath))
            return;

        var executionDevice = ThemeService.GetAutoMaskExecutionDevice();
        bool useMultiPass = ThemeService.GetAutoMaskMultiPassEnabled();

        await RunMaskSelectionAsync(
            "Status.AutoSelectingMask", "Selecting mask with AI...",
            "AI自動選択に失敗: image-wide selection",
            "Dialog.AIAutoMask.Failed", "Failed to select mask with AI:\n{0}",
            "Status.AIAutoMaskError", "AI auto mask error",
            input =>
            {
                try
                {
                    var r = _autoMaskSelector.CreateMask(input, modelPath, useMultiPass: useMultiPass, executionDevice);
                    return (r.Mask, r.Width, r.Height);
                }
                finally { input.Dispose(); }
            },
            (mask, w, h) => { ApplySelectedMaskFromBinary(mask, w, h); return true; });
    }

    public async Task<bool> AutoSelectMaskAtAsync(double imageX, double imageY)
    {
        var targetKind = ThemeService.GetAutoMaskTargetKind();
        var model = AutoMaskModelCatalog.GetForTarget(targetKind);
        var modelPath = await EnsureAutoMaskModelPathAsync(model);
        if (string.IsNullOrWhiteSpace(modelPath))
            return false;

        var executionDevice = ThemeService.GetAutoMaskExecutionDevice();
        bool useMultiPass = ThemeService.GetAutoMaskMultiPassEnabled();
        int seedX = Math.Clamp((int)Math.Round(imageX), 0, ImageWidth - 1);
        int seedY = Math.Clamp((int)Math.Round(imageY), 0, ImageHeight - 1);

        return await RunMaskSelectionAsync(
            "Status.AutoSelectingMask", "Selecting mask with AI...",
            $"AI自動選択に失敗: x={seedX}, y={seedY}",
            "Dialog.AIAutoMask.Failed", "Failed to select mask with AI:\n{0}",
            "Status.AIAutoMaskError", "AI auto mask error",
            input =>
            {
                try
                {
                    var r = _autoMaskSelector.CreateMask(input, modelPath, seedX, seedY, useMultiPass, executionDevice);
                    return (r.Mask, r.Width, r.Height);
                }
                finally { input.Dispose(); }
            },
            (mask, w, h) => IsMaskEraseMode
                ? SubtractSelectedMaskFromBinary(mask, w, h)
                : MergeSelectedMaskFromBinary(mask, w, h));
    }

    public async Task<bool> AutoSelectMaskByColorAtAsync(double imageX, double imageY)
    {
        int seedX = Math.Clamp((int)Math.Round(imageX), 0, ImageWidth - 1);
        int seedY = Math.Clamp((int)Math.Round(imageY), 0, ImageHeight - 1);
        int colorError = Math.Clamp(MaskColorError, 0, 80);
        int gapClosing = Math.Clamp(MaskGapClosing, 0, 6);
        bool antialias = IsMaskAutoSelectAntialiasEnabled;
        int connectivity = MaskColorConnectivity == 8 ? 8 : 4;

        return await RunMaskSelectionAsync(
            "Status.AutoSelectingMaskByColor", "Selecting mask by color...",
            $"色ベース自動選択に失敗: x={seedX}, y={seedY}, colorError={colorError}, gapClosing={gapClosing}, connectivity={connectivity}, antialias={antialias}",
            "Dialog.AutoMaskByColor.Failed", "Failed to auto-select mask by color:\n{0}",
            "Status.AutoMaskByColorError", "Auto select error",
            input =>
            {
                try
                {
                    var mask = MaskProcessingService.BuildColorSelectionMask(
                        input, seedX, seedY, colorError, connectivity, gapClosing, antialias);
                    return (mask, ImageWidth, ImageHeight);
                }
                finally { input.Dispose(); }
            },
            (mask, w, h) => IsMaskEraseMode
                ? SubtractSelectedMaskFromBinary(mask, w, h)
                : AppendSelectedMaskFromBinary(mask, w, h));
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
        RestoreAdjustmentValues(AdjustmentValues.Default);
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
        RestoreAdjustmentValues(preset.Adjustments);
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
