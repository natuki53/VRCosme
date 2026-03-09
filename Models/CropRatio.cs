namespace VRCosme.Models;

public record CropRatioItem(string Name, double WidthRatio, double HeightRatio)
{
    public bool IsNone => WidthRatio == 0 && HeightRatio == 0;
    public bool IsFree => !IsNone && (WidthRatio <= 0 || HeightRatio <= 0);
    public double AspectRatio => IsNone || IsFree ? 0 : WidthRatio / HeightRatio;
    public override string ToString() => Name;
}
