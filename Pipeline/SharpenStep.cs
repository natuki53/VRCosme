using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VRCosme.Services.ImageProcessing;

namespace VRCosme.Pipeline;

internal sealed class SharpenStep : IAdjustmentStep
{
    public void Apply(Image<Rgba32> image, AdjustmentParams parameters)
    {
        if (parameters.Sharpen <= 0.5f) return;

        float sigma = parameters.Sharpen / 100f * 3f + 0.3f;
        image.Mutate(ctx => ctx.GaussianSharpen(sigma));
    }
}
