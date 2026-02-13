using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
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

    [RelayCommand]
    private async Task AutoSelectMaskAsync()
    {
        if (!HasImage || _transformedImage == null || ImageWidth <= 0 || ImageHeight <= 0)
            return;
        if (IsProcessing)
            return;

        var targetKind = ThemeService.GetAutoMaskTargetKind();
        var model = AutoMaskModelCatalog.GetForTarget(targetKind);
        var modelPath = await EnsureAutoMaskModelPathAsync(model);
        if (string.IsNullOrWhiteSpace(modelPath))
            return;

        var executionDevice = ThemeService.GetAutoMaskExecutionDevice();
        bool useMultiPass = ThemeService.GetAutoMaskMultiPassEnabled();

        IsProcessing = true;
        StatusMessage = LocalizationService.GetString("Status.AutoSelectingMask", "Selecting mask with AI...");

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
            var result = await Task.Run(() =>
            {
                try
                {
                    return _autoMaskSelector.CreateMask(input, modelPath, useMultiPass: useMultiPass, executionDevice);
                }
                finally
                {
                    input.Dispose();
                }
            });

            ApplySelectedMaskFromBinary(result.Mask, result.Width, result.Height);
            if (createdMaskLayer && snapshotBeforeOperation is not null)
                ReplaceLatestUndoState(snapshotBeforeOperation);
            StatusMessage = BuildReadyStatusMessage();
        }
        catch (Exception ex)
        {
            LogService.Error("AI自動選択に失敗: image-wide selection", ex);
            MessageBox.Show(
                LocalizationService.Format("Dialog.AIAutoMask.Failed", "Failed to select mask with AI:\n{0}", ex.Message),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusMessage = LocalizationService.GetString("Status.AIAutoMaskError", "AI auto mask error");
        }
        finally
        {
            IsProcessing = false;
        }
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

        var executionDevice = ThemeService.GetAutoMaskExecutionDevice();
        bool useMultiPass = ThemeService.GetAutoMaskMultiPassEnabled();
        int seedX = Math.Clamp((int)Math.Round(imageX), 0, ImageWidth - 1);
        int seedY = Math.Clamp((int)Math.Round(imageY), 0, ImageHeight - 1);

        IsProcessing = true;
        StatusMessage = LocalizationService.GetString("Status.AutoSelectingMask", "Selecting mask with AI...");

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
            var result = await Task.Run(() =>
            {
                try
                {
                    return _autoMaskSelector.CreateMask(input, modelPath, seedX, seedY, useMultiPass, executionDevice);
                }
                finally
                {
                    input.Dispose();
                }
            });

            bool changed = IsMaskEraseMode
                ? SubtractSelectedMaskFromBinary(result.Mask, result.Width, result.Height)
                : MergeSelectedMaskFromBinary(result.Mask, result.Width, result.Height);
            if (changed && createdMaskLayer && snapshotBeforeOperation is not null)
                ReplaceLatestUndoState(snapshotBeforeOperation);
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

    public async Task<bool> AutoSelectMaskByColorAtAsync(double imageX, double imageY)
    {
        if (!HasImage || _transformedImage == null || ImageWidth <= 0 || ImageHeight <= 0)
            return false;
        if (IsProcessing)
            return false;

        int seedX = Math.Clamp((int)Math.Round(imageX), 0, ImageWidth - 1);
        int seedY = Math.Clamp((int)Math.Round(imageY), 0, ImageHeight - 1);
        int colorError = Math.Clamp(MaskColorError, 0, 80);
        int gapClosing = Math.Clamp(MaskGapClosing, 0, 6);
        bool antialias = IsMaskAutoSelectAntialiasEnabled;
        int connectivity = MaskColorConnectivity == 8 ? 8 : 4;

        IsProcessing = true;
        StatusMessage = LocalizationService.GetString(
            "Status.AutoSelectingMaskByColor",
            "Selecting mask by color...");

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
            var mask = await Task.Run(() =>
            {
                try
                {
                    return BuildColorSelectionMask(
                        input,
                        seedX,
                        seedY,
                        colorError,
                        connectivity,
                        gapClosing,
                        antialias);
                }
                finally
                {
                    input.Dispose();
                }
            });

            bool changed = IsMaskEraseMode
                ? SubtractSelectedMaskFromBinary(mask, ImageWidth, ImageHeight)
                : AppendSelectedMaskFromBinary(mask, ImageWidth, ImageHeight);
            if (changed && createdMaskLayer && snapshotBeforeOperation is not null)
                ReplaceLatestUndoState(snapshotBeforeOperation);
            StatusMessage = BuildReadyStatusMessage();
            return changed;
        }
        catch (Exception ex)
        {
            LogService.Error(
                $"色ベース自動選択に失敗: x={seedX}, y={seedY}, colorError={colorError}, gapClosing={gapClosing}, connectivity={connectivity}, antialias={antialias}",
                ex);
            MessageBox.Show(
                LocalizationService.Format(
                    "Dialog.AutoMaskByColor.Failed",
                    "Failed to auto-select mask by color:\n{0}",
                    ex.Message),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusMessage = LocalizationService.GetString(
                "Status.AutoMaskByColorError",
                "Auto select error");
            return false;
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private static byte[] BuildColorSelectionMask(
        Image<Rgba32> source,
        int seedX,
        int seedY,
        int colorError,
        int connectivity,
        int gapClosing,
        bool antialias)
    {
        int width = source.Width;
        int height = source.Height;
        int pixelCount = width * height;
        var mask = new byte[pixelCount];
        if (pixelCount == 0)
            return mask;

        seedX = Math.Clamp(seedX, 0, width - 1);
        seedY = Math.Clamp(seedY, 0, height - 1);
        colorError = Math.Clamp(colorError, 0, 80);
        connectivity = connectivity == 8 ? 8 : 4;
        gapClosing = Math.Clamp(gapClosing, 0, 6);

        var red = new byte[pixelCount];
        var green = new byte[pixelCount];
        var blue = new byte[pixelCount];
        for (int y = 0; y < height; y++)
        {
            var row = source.DangerousGetPixelRowMemory(y).Span;
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                red[idx] = row[x].R;
                green[idx] = row[x].G;
                blue[idx] = row[x].B;
            }
        }

        int seedIndex = seedY * width + seedX;
        var (seedR, seedG, seedB) = ComputeSeedMedianColor(red, green, blue, width, height, seedX, seedY);
        int neighborTolerance = Math.Clamp((int)Math.Round(colorError * 0.55), 2, 64);

        var visited = new bool[pixelCount];
        var queue = new int[pixelCount];
        int head = 0;
        int tail = 0;

        visited[seedIndex] = true;
        queue[tail++] = seedIndex;

        while (head < tail)
        {
            int idx = queue[head++];
            mask[idx] = MaskOnValue;

            int x = idx % width;
            int y = idx / width;

            EnqueueIfMatch(x - 1, y, idx);
            EnqueueIfMatch(x + 1, y, idx);
            EnqueueIfMatch(x, y - 1, idx);
            EnqueueIfMatch(x, y + 1, idx);

            if (connectivity == 8)
            {
                EnqueueIfMatch(x - 1, y - 1, idx);
                EnqueueIfMatch(x + 1, y - 1, idx);
                EnqueueIfMatch(x - 1, y + 1, idx);
                EnqueueIfMatch(x + 1, y + 1, idx);
            }
        }

        if (gapClosing > 0)
            ApplyGapClosing(mask, width, height, connectivity, gapClosing);

        SuppressMaskNoise(mask, width, height, connectivity, colorError);

        if (antialias)
            ApplyMaskAntialias(mask, width, height, connectivity);

        mask[seedIndex] = MaskOnValue;
        return mask;

        void EnqueueIfMatch(int nx, int ny, int fromIndex)
        {
            if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                return;

            int n = ny * width + nx;
            if (visited[n])
                return;

            visited[n] = true;
            if (!IsColorWithinTolerance(red[n], green[n], blue[n], seedR, seedG, seedB, colorError))
                return;
            if (!IsColorWithinTolerance(red[n], green[n], blue[n], red[fromIndex], green[fromIndex], blue[fromIndex], neighborTolerance))
                return;

            queue[tail++] = n;
        }
    }

    private static bool IsColorWithinTolerance(
        byte r,
        byte g,
        byte b,
        byte seedR,
        byte seedG,
        byte seedB,
        int tolerance)
    {
        return Math.Abs(r - seedR) <= tolerance
            && Math.Abs(g - seedG) <= tolerance
            && Math.Abs(b - seedB) <= tolerance;
    }

    private static (byte R, byte G, byte B) ComputeSeedMedianColor(
        byte[] red,
        byte[] green,
        byte[] blue,
        int width,
        int height,
        int seedX,
        int seedY)
    {
        var rs = new int[9];
        var gs = new int[9];
        var bs = new int[9];
        int count = 0;

        for (int oy = -1; oy <= 1; oy++)
        {
            int y = seedY + oy;
            if ((uint)y >= (uint)height)
                continue;

            int rowOffset = y * width;
            for (int ox = -1; ox <= 1; ox++)
            {
                int x = seedX + ox;
                if ((uint)x >= (uint)width)
                    continue;

                int idx = rowOffset + x;
                rs[count] = red[idx];
                gs[count] = green[idx];
                bs[count] = blue[idx];
                count++;
            }
        }

        if (count <= 0)
        {
            int index = seedY * width + seedX;
            return (red[index], green[index], blue[index]);
        }

        Array.Sort(rs, 0, count);
        Array.Sort(gs, 0, count);
        Array.Sort(bs, 0, count);
        int mid = count / 2;
        return ((byte)rs[mid], (byte)gs[mid], (byte)bs[mid]);
    }

    private static void SuppressMaskNoise(byte[] mask, int width, int height, int connectivity, int colorError)
    {
        if (mask.Length == 0 || width < 3 || height < 3)
            return;

        connectivity = connectivity == 8 ? 8 : 4;
        int passes = colorError >= 24 ? 2 : 1;
        int fillThreshold = connectivity == 8 ? 7 : 4;
        var snapshot = new byte[mask.Length];

        for (int pass = 0; pass < passes; pass++)
        {
            Buffer.BlockCopy(mask, 0, snapshot, 0, mask.Length);

            for (int y = 1; y < height - 1; y++)
            {
                int rowOffset = y * width;
                for (int x = 1; x < width - 1; x++)
                {
                    int idx = rowOffset + x;
                    int neighborCount = CountSelectedNeighbors(snapshot, width, idx, connectivity);

                    if (snapshot[idx] == MaskOnValue)
                    {
                        if (neighborCount <= 1)
                            mask[idx] = MaskOffValue;
                    }
                    else
                    {
                        if (neighborCount >= fillThreshold)
                            mask[idx] = MaskOnValue;
                    }
                }
            }
        }
    }

    private static void ApplyGapClosing(byte[] mask, int width, int height, int connectivity, int steps)
    {
        if (steps <= 0 || mask.Length == 0 || width <= 1 || height <= 1)
            return;

        connectivity = connectivity == 8 ? 8 : 4;
        steps = Math.Clamp(steps, 0, 6);
        var temp = new byte[mask.Length];

        for (int i = 0; i < steps; i++)
        {
            DilateMask(mask, temp, width, height, connectivity);
            ErodeMask(temp, mask, width, height, connectivity);
        }
    }

    private static void DilateMask(byte[] source, byte[] destination, int width, int height, int connectivity)
    {
        Array.Clear(destination, 0, destination.Length);

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                if (source[idx] == 0)
                    continue;

                destination[idx] = MaskOnValue;
                if (x > 0) destination[idx - 1] = MaskOnValue;
                if (x + 1 < width) destination[idx + 1] = MaskOnValue;
                if (y > 0) destination[idx - width] = MaskOnValue;
                if (y + 1 < height) destination[idx + width] = MaskOnValue;

                if (connectivity == 8)
                {
                    if (x > 0 && y > 0) destination[idx - width - 1] = MaskOnValue;
                    if (x + 1 < width && y > 0) destination[idx - width + 1] = MaskOnValue;
                    if (x > 0 && y + 1 < height) destination[idx + width - 1] = MaskOnValue;
                    if (x + 1 < width && y + 1 < height) destination[idx + width + 1] = MaskOnValue;
                }
            }
        }
    }

    private static void ErodeMask(byte[] source, byte[] destination, int width, int height, int connectivity)
    {
        Array.Clear(destination, 0, destination.Length);

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                if (source[idx] == 0)
                    continue;

                bool keep = source[idx] > 0
                    && IsOn(source, width, height, x - 1, y)
                    && IsOn(source, width, height, x + 1, y)
                    && IsOn(source, width, height, x, y - 1)
                    && IsOn(source, width, height, x, y + 1);

                if (keep && connectivity == 8)
                {
                    keep = IsOn(source, width, height, x - 1, y - 1)
                        && IsOn(source, width, height, x + 1, y - 1)
                        && IsOn(source, width, height, x - 1, y + 1)
                        && IsOn(source, width, height, x + 1, y + 1);
                }

                if (keep)
                    destination[idx] = MaskOnValue;
            }
        }
    }

    private static bool IsOn(byte[] data, int width, int height, int x, int y)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            return false;

        return data[y * width + x] > 0;
    }

    private static void ApplyMaskAntialias(byte[] mask, int width, int height, int connectivity)
    {
        if (mask.Length == 0 || width < 3 || height < 3)
            return;

        connectivity = connectivity == 8 ? 8 : 4;
        var snapshot = (byte[])mask.Clone();
        int maxNeighbors = connectivity == 8 ? 8 : 4;

        for (int y = 1; y < height - 1; y++)
        {
            int rowOffset = y * width;
            for (int x = 1; x < width - 1; x++)
            {
                int idx = rowOffset + x;
                if (snapshot[idx] == 0)
                    continue;

                int neighborCount = CountSelectedNeighbors(snapshot, width, idx, connectivity);
                if (neighborCount >= maxNeighbors)
                    continue;

                int softened = connectivity == 8
                    ? 120 + (neighborCount * 16)
                    : 136 + (neighborCount * 24);
                mask[idx] = (byte)Math.Clamp(softened, 96, 255);
            }
        }
    }

    private static int CountSelectedNeighbors(byte[] mask, int width, int idx, int connectivity)
    {
        int count = 0;

        if (mask[idx - width] == MaskOnValue) count++;
        if (mask[idx + width] == MaskOnValue) count++;
        if (mask[idx - 1] == MaskOnValue) count++;
        if (mask[idx + 1] == MaskOnValue) count++;

        if (connectivity == 8)
        {
            if (mask[idx - width - 1] == MaskOnValue) count++;
            if (mask[idx - width + 1] == MaskOnValue) count++;
            if (mask[idx + width - 1] == MaskOnValue) count++;
            if (mask[idx + width + 1] == MaskOnValue) count++;
        }

        return count;
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
