using VRCosme.Services.ImageProcessing;

namespace VRCosme.Models;

/// <summary>13個の補正パラメータを集約する値型</summary>
public readonly record struct AdjustmentValues(
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
    double Vignette)
{
    public static AdjustmentValues Default { get; } = new(0, 0, 1.0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>UI スライダー値 → ImageProcessor 用パラメータに変換</summary>
    public AdjustmentParams ToParams() => new(
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
}
