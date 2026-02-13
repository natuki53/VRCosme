using CommunityToolkit.Mvvm.Input;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.IO;
using System.Windows;
using VRCosme.Models;
using VRCosme.Services;
using VRCosme.Services.ImageProcessing;

namespace VRCosme.ViewModels;

public partial class MainViewModel
{
    // ───────── 補正パラメータ生成 ─────────

    private AdjustmentParams BuildParams() => new(
        Brightness: 1.0f + (float)(Brightness / 100.0),
        Contrast: 1.0f + (float)(Contrast / 100.0),
        Gamma: (float)Gamma,
        Exposure: (float)Exposure,
        Saturation: 1.0f + (float)(Saturation / 100.0),
        Temperature: (float)Temperature,
        Tint: (float)Tint,
        Shadows: (float)Shadows,
        Highlights: (float)Highlights,
        Clarity: (float)Clarity,
        Sharpen: (float)Sharpen,
        Vignette: (float)Vignette,
        Blur: (float)Blur
    );

    // ───────── トリミング ─────────

    public void InitializeCropRect(CropRatioItem ratio)
    {
        if (_transformedImage == null) return;
        double imgAspect = (double)ImageWidth / ImageHeight;
        double cropAspect = ratio.AspectRatio;

        double cropW, cropH;
        if (cropAspect > imgAspect) { cropW = ImageWidth; cropH = ImageWidth / cropAspect; }
        else { cropH = ImageHeight; cropW = ImageHeight * cropAspect; }

        CropX = (ImageWidth - cropW) / 2;
        CropY = (ImageHeight - cropH) / 2;
        CropWidth = cropW;
        CropHeight = cropH;
    }

    // ───────── 画像読み込み ─────────

    public async Task LoadImageAsync(string filePath)
    {
        IsProcessing = true;
        StatusMessage = LocalizationService.GetString("Status.LoadingImage", "Loading image...");
        try
        {
            Image<Rgba32>? oldPristine;
            Image<Rgba32>? oldTransformed;
            Image<Rgba32>? oldPreview;
            lock (_imageSync)
            {
                oldPristine = _pristineImage;
                oldTransformed = _transformedImage;
                oldPreview = _previewSourceImage;
                _pristineImage = null;
                _transformedImage = null;
                _previewSourceImage = null;
            }

            oldPristine?.Dispose();
            oldTransformed?.Dispose();
            oldPreview?.Dispose();

            LogService.Info($"画像読み込み開始: {filePath}");

            var loadedPristine = await Task.Run(() => ImageProcessor.LoadImage(filePath));
            _rotationDegrees = 0;
            _flipHorizontal = false;
            _flipVertical = false;
            IsMaskEditing = false;
            IsMaskEraseMode = false;
            ResetMaskForNewImage();

            var transformed = loadedPristine.Clone();
            var preview = await Task.Run(() =>
                ImageProcessor.CreatePreview(transformed, MaxPreviewDimension));
            lock (_imageSync)
            {
                _pristineImage = loadedPristine;
                _transformedImage = transformed;
                _previewSourceImage = preview;
            }

            SourceFilePath = filePath;
            ImageWidth = transformed.Width;
            ImageHeight = transformed.Height;
            PreviewScale = (double)preview.Width / transformed.Width;
            EnsureMaskSizeMatchesImageOrClear();
            WindowTitle = BuildWindowTitle(filePath);

            ResetAllAdjustments();
            SelectedCropRatio = CropRatios[0];
            IsCropActive = false;
            CompareMode = CompareMode.After;

            await UpdatePreviewAsync();
            await GenerateBeforeBitmapAsync();

            RecentFilesService.Add(filePath);
            LoadRecentFiles();

            _undoStack.Clear();
            _redoStack.Clear();
            NotifyUndoRedoChanged();

            LogService.Info($"画像読み込み完了: {ImageWidth}×{ImageHeight} px, プレビュースケール={PreviewScale:F3}");
            StatusMessage = BuildReadyStatusMessage();
        }
        catch (Exception ex)
        {
            LogService.Error($"画像読み込み失敗: {filePath}", ex);
            MessageBox.Show(
                LocalizationService.Format("Dialog.LoadImage.Failed", "Failed to load image:\n{0}", ex.Message),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusMessage = LocalizationService.GetString("Status.LoadError", "Load error");
        }
        finally { IsProcessing = false; }
    }

    // ───────── 回転・反転 ─────────

    private async Task ApplyTransformAsync()
    {
        Image<Rgba32>? pristine;
        lock (_imageSync)
        {
            pristine = _pristineImage;
        }
        if (pristine == null) return;
        IsProcessing = true;
        StatusMessage = LocalizationService.GetString("Status.ApplyingTransform", "Applying transform...");
        try
        {
            var transformed = await Task.Run(() =>
                ImageProcessor.ApplyTransform(pristine, _rotationDegrees, _flipHorizontal, _flipVertical));

            var preview = await Task.Run(() =>
                ImageProcessor.CreatePreview(transformed, MaxPreviewDimension));

            Image<Rgba32>? oldTransformed;
            Image<Rgba32>? oldPreview;
            lock (_imageSync)
            {
                oldTransformed = _transformedImage;
                oldPreview = _previewSourceImage;
                _transformedImage = transformed;
                _previewSourceImage = preview;
            }
            oldTransformed?.Dispose();
            oldPreview?.Dispose();

            ImageWidth = transformed.Width;
            ImageHeight = transformed.Height;
            PreviewScale = (double)preview.Width / transformed.Width;

            if (IsCropActive) InitializeCropRect(SelectedCropRatio);

            await UpdatePreviewAsync();
            await GenerateBeforeBitmapAsync();
            StatusMessage = BuildReadyStatusMessage();
        }
        catch (Exception ex)
        {
            LogService.Error("変換処理中にエラー", ex);
            StatusMessage = LocalizationService.GetString("Status.TransformError", "Transform error");
        }
        finally { IsProcessing = false; }
    }

    [RelayCommand]
    private async Task RotateClockwiseAsync()
    {
        if (!HasImage) return;
        PushUndoSnapshot();
        RotateMaskClockwise90();
        _rotationDegrees = (_rotationDegrees + 90) % 360;
        LogService.Info($"右90°回転 (累計={_rotationDegrees}°)");
        await ApplyTransformAsync();
    }

    [RelayCommand]
    private async Task RotateCounterClockwiseAsync()
    {
        if (!HasImage) return;
        PushUndoSnapshot();
        RotateMaskCounterClockwise90();
        _rotationDegrees = (_rotationDegrees + 270) % 360;
        LogService.Info($"左90°回転 (累計={_rotationDegrees}°)");
        await ApplyTransformAsync();
    }

    [RelayCommand]
    private async Task FlipHorizontalAsync()
    {
        if (!HasImage) return;
        PushUndoSnapshot();
        FlipMaskHorizontal();
        _flipHorizontal = !_flipHorizontal;
        LogService.Info($"左右反転 ({_flipHorizontal})");
        await ApplyTransformAsync();
    }

    [RelayCommand]
    private async Task FlipVerticalAsync()
    {
        if (!HasImage) return;
        PushUndoSnapshot();
        FlipMaskVertical();
        _flipVertical = !_flipVertical;
        LogService.Info($"上下反転 ({_flipVertical})");
        await ApplyTransformAsync();
    }

    // ───────── プレビュー更新 ─────────

    private async void SchedulePreviewUpdate()
    {
        _previewUpdatePending = true;

        if (_previewUpdateRunning) return;
        _previewUpdateRunning = true;
        try
        {
            while (_previewUpdatePending)
            {
                _previewUpdatePending = false;
                await UpdatePreviewAsync();
            }
        }
        catch (Exception ex)
        {
            LogService.Error("プレビュー更新中にエラー", ex);
        }
        finally
        {
            _previewUpdateRunning = false;
        }
    }

    public async Task UpdatePreviewAsync()
    {
        Image<Rgba32>? source;
        lock (_imageSync)
        {
            source = _previewSourceImage?.Clone();
        }
        if (source == null) return;

        var p = BuildParams();
        int previewWidth = source.Width;
        int previewHeight = source.Height;
        var layerAdjustmentSequence = BuildMaskAdjustmentSequence(
            previewWidth,
            previewHeight,
            requireMaskEnabled: false);

        var renderingStatus = LocalizationService.GetString("Status.RenderingPreview", "Rendering preview...");
        var previousStatus = StatusMessage;
        bool shouldManagePreviewStatus =
            !IsProcessing
            || string.IsNullOrWhiteSpace(previousStatus)
            || previousStatus == renderingStatus;
        if (shouldManagePreviewStatus)
            StatusMessage = renderingStatus;

        var result = await Task.Run(() =>
        {
            Image<Rgba32>? current = null;
            try
            {
                current = ImageProcessor.ApplyAdjustments(source, p);
                foreach (var (mask, layerParams, naturalizeBoundary) in layerAdjustmentSequence)
                {
                    var next = ImageProcessor.ApplyAdjustments(current, layerParams, mask, naturalizeBoundary);
                    current.Dispose();
                    current = next;
                }

                var previewBitmap = ImageProcessor.ToBitmapSource(current);
                return previewBitmap;
            }
            finally
            {
                current?.Dispose();
                source.Dispose();
            }
        });

        PreviewBitmap = result;
        if (shouldManagePreviewStatus)
        {
            StatusMessage = IsProcessing
                ? previousStatus
                : BuildReadyStatusMessage();
        }
    }

    private async Task GenerateBeforeBitmapAsync()
    {
        Image<Rgba32>? source;
        lock (_imageSync)
        {
            source = _previewSourceImage?.Clone();
        }
        if (source == null) return;
        var bitmap = await Task.Run(() =>
        {
            var bmp = ImageProcessor.ToBitmapSource(source);
            source.Dispose();
            return bmp;
        });
        BeforeBitmap = bitmap;
    }
}
