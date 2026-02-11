using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VRCosme.Services.ImageProcessing;

namespace VRCosme.Pipeline;

internal sealed class SaturationStep : IAdjustmentStep
{
    public void Apply(Image<Rgba32> image, AdjustmentParams parameters)
    {
        if (MathF.Abs(parameters.Saturation - 1f) <= 0.001f) return;
        image.Mutate(ctx => ctx.Saturate(parameters.Saturation));
    }
}
