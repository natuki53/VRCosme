namespace VRCosme.Models;

public record MaskLayerState(
    string Name,
    byte[] MaskData,
    int Width,
    int Height,
    int NonZeroCount,
    AdjustmentValues Adjustments,
    bool NaturalizeBoundary
);
