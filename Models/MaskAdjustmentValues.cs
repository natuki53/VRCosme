namespace VRCosme.Models;

public readonly record struct MaskAdjustmentValues(
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
    double Vignette
);
