namespace VRCosme.Models;

/// <summary>
/// 編集状態のスナップショット。Undo/Redo 用に補正・トリム・回転・反転・マスクを保持する。
/// </summary>
public record EditState(
    double Brightness,
    double Contrast,
    double Gamma,
    double Exposure,
    double Saturation,
    double Temperature,
    double Tint,
    double Shadows,
    double Highlights,
    double Clarity,
    double Blur,
    double Sharpen,
    double Vignette,
    bool IsCropActive,
    double CropX,
    double CropY,
    double CropWidth,
    double CropHeight,
    int SelectedCropRatioIndex,
    int RotationDegrees,
    bool FlipHorizontal,
    bool FlipVertical,
    bool IsMaskEnabled,
    int SelectedMaskLayerIndex,
    IReadOnlyList<MaskLayerState> MaskLayers
);
