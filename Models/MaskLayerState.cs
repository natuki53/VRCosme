namespace VRCosme.Models;

public record MaskLayerState(
    string Name,
    byte[] MaskData,
    int Width,
    int Height,
    int NonZeroCount,
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
    bool NaturalizeBoundary
);
