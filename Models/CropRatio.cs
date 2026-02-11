namespace VRCosme.Models;

public record CropRatioItem(string Name, double WidthRatio, double HeightRatio)
{
    public bool IsFree => WidthRatio <= 0 || HeightRatio <= 0;
    public double AspectRatio => IsFree ? 0 : WidthRatio / HeightRatio;
    public override string ToString() => Name;
}
