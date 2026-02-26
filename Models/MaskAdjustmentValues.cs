namespace VRCosme.Models;

public readonly record struct MaskAdjustmentValues(
    AdjustmentValues Adjustments,
    bool NaturalizeBoundary)
{
    // Convenience accessors for backward compatibility with dialog code
    public double Brightness => Adjustments.Brightness;
    public double Contrast => Adjustments.Contrast;
    public double Gamma => Adjustments.Gamma;
    public double Exposure => Adjustments.Exposure;
    public double Saturation => Adjustments.Saturation;
    public double Temperature => Adjustments.Temperature;
    public double Tint => Adjustments.Tint;
    public double Shadows => Adjustments.Shadows;
    public double Highlights => Adjustments.Highlights;
    public double Clarity => Adjustments.Clarity;
    public double Blur => Adjustments.Blur;
    public double Sharpen => Adjustments.Sharpen;
    public double Vignette => Adjustments.Vignette;
}
