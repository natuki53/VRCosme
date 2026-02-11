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
            _pristineImage?.Dispose();
            _transformedImage?.Dispose();
            _previewSourceImage?.Dispose();

            LogService.Info($"画像読み込み開始: {filePath}");

            _pristineImage = await Task.Run(() => ImageProcessor.LoadImage(filePath));
            _rotationDegrees = 0;
            _flipHorizontal = false;
            _flipVertical = false;
            IsMaskEditing = false;
            IsMaskEraseMode = false;
            ResetMaskForNewImage();

            _transformedImage = _pristineImage.Clone();
            _previewSourceImage = await Task.Run(() =>
                ImageProcessor.CreatePreview(_transformedImage, MaxPreviewDimension));

            SourceFilePath = filePath;
            ImageWidth = _transformedImage.Width;
            ImageHeight = _transformedImage.Height;
            PreviewScale = (double)_previewSourceImage.Width / _transformedImage.Width;
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
        if (_pristineImage == null) return;
        IsProcessing = true;
        StatusMessage = LocalizationService.GetString("Status.ApplyingTransform", "Applying transform...");
        try
        {
            _transformedImage?.Dispose();
            _transformedImage = await Task.Run(() =>
                ImageProcessor.ApplyTransform(_pristineImage, _rotationDegrees, _flipHorizontal, _flipVertical));

            _previewSourceImage?.Dispose();
            _previewSourceImage = await Task.Run(() =>
                ImageProcessor.CreatePreview(_transformedImage, MaxPreviewDimension));

            ImageWidth = _transformedImage.Width;
            ImageHeight = _transformedImage.Height;
            PreviewScale = (double)_previewSourceImage.Width / _transformedImage.Width;

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
        if (_previewSourceImage == null) return;

        var p = BuildParams();
        var source = _previewSourceImage.Clone();
        int previewWidth = source.Width;
        int previewHeight = source.Height;
        var layerAdjustmentSequence = BuildMaskAdjustmentSequence(
            previewWidth,
            previewHeight,
            requireMaskEnabled: false);

        StatusMessage = LocalizationService.GetString("Status.RenderingPreview", "Rendering preview...");
        var result = await Task.Run(() =>
        {
            Image<Rgba32>? current = null;
            try
            {
                current = ImageProcessor.ApplyAdjustments(source, p);
                foreach (var (mask, layerParams) in layerAdjustmentSequence)
                {
                    var next = ImageProcessor.ApplyAdjustments(current, layerParams, mask);
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
        StatusMessage = BuildReadyStatusMessage();
    }

    private async Task GenerateBeforeBitmapAsync()
    {
        if (_previewSourceImage == null) return;
        var source = _previewSourceImage.Clone();
        var bitmap = await Task.Run(() =>
        {
            var bmp = ImageProcessor.ToBitmapSource(source);
            source.Dispose();
            return bmp;
        });
        BeforeBitmap = bitmap;
    }
}
