namespace VRCosme.Models;

/// <summary>
/// 編集途中の作業状態を保存するセッションドキュメント。
/// </summary>
public sealed class SessionDocument
{
    public int Version { get; set; } = 2;
    public string SourceFilePath { get; set; } = "";
    public string SourceFilePathRelative { get; set; } = "";

    public AdjustmentValues Adjustments { get; set; } = AdjustmentValues.Default;
    public bool IsCropActive { get; set; }
    public double CropX { get; set; }
    public double CropY { get; set; }
    public double CropWidth { get; set; }
    public double CropHeight { get; set; }
    public bool IsCropApplied { get; set; }
    public int SelectedCropRatioIndex { get; set; }
    public int RotationDegrees { get; set; }
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public bool IsMaskEnabled { get; set; } = true;
    public int SelectedMaskLayerIndex { get; set; } = -1;
    public List<MaskLayerState> MaskLayers { get; set; } = [];
}
