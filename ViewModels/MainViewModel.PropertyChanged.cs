using VRCosme.Models;
using VRCosme.Services;

namespace VRCosme.ViewModels;

public partial class MainViewModel
{
    // ───────── プロパティ変更時 → プレビュー更新 ─────────

    partial void OnBrightnessChanged(double value) => SchedulePreviewUpdate();
    partial void OnContrastChanged(double value) => SchedulePreviewUpdate();
    partial void OnGammaChanged(double value) => SchedulePreviewUpdate();
    partial void OnExposureChanged(double value) => SchedulePreviewUpdate();
    partial void OnSaturationChanged(double value) => SchedulePreviewUpdate();
    partial void OnTemperatureChanged(double value) => SchedulePreviewUpdate();
    partial void OnTintChanged(double value) => SchedulePreviewUpdate();
    partial void OnShadowsChanged(double value) => SchedulePreviewUpdate();
    partial void OnHighlightsChanged(double value) => SchedulePreviewUpdate();
    partial void OnClarityChanged(double value) => SchedulePreviewUpdate();
    partial void OnBlurChanged(double value) => SchedulePreviewUpdate();
    partial void OnSharpenChanged(double value) => SchedulePreviewUpdate();
    partial void OnVignetteChanged(double value) => SchedulePreviewUpdate();
    partial void OnIsMaskEnabledChanged(bool value)
    {
        MarkCurrentSessionDirty();
        SchedulePreviewUpdate();
    }

    partial void OnSourceFilePathChanged(string? value) =>
        WindowTitle = BuildWindowTitle(value);

    partial void OnIsDirtyChanged(bool value) =>
        WindowTitle = BuildWindowTitle(SourceFilePath);

    partial void OnSelectedExportFormatChanged(string value) =>
        OnPropertyChanged(nameof(IsJpegSelected));

    partial void OnCompareModeChanged(CompareMode value) =>
        CompareModeChanged?.Invoke();

    partial void OnIsFitToScreenChanged(bool value) =>
        ZoomModeChanged?.Invoke();

    partial void OnShowRuleOfThirdsGridChanged(bool value) =>
        ThemeService.SaveShowRuleOfThirdsGrid(value);

    partial void OnShowRulerChanged(bool value) =>
        ThemeService.SaveShowRuler(value);

    partial void OnIsCropActiveChanged(bool value)
    {
        if (!value)
            InvalidateCropApplyState();
    }

    partial void OnCropXChanged(double value) => InvalidateCropApplyState();
    partial void OnCropYChanged(double value) => InvalidateCropApplyState();
    partial void OnCropWidthChanged(double value) => InvalidateCropApplyState();
    partial void OnCropHeightChanged(double value) => InvalidateCropApplyState();

    private void InvalidateCropApplyState()
    {
        if (_isRestoringState || !HasImage)
            return;

        if (IsCropApplied)
            IsCropApplied = false;
    }

    partial void OnSelectedCropRatioChanged(CropRatioItem value)
    {
        if (_isRelocalizing) return;
        if (!HasImage) return;
        PushUndoSnapshot();
        if (value.IsNone)
        {
            IsCropActive = false;
        }
        else
        {
            IsCropActive = true;
            if (value.IsFree)
                InitializeFreeCropRect();
            else
                InitializeCropRect(value);
        }
        InvalidateCropApplyState();
    }
}
