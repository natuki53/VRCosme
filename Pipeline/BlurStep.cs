using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VRCosme.Services.ImageProcessing;

namespace VRCosme.Pipeline;

internal sealed class BlurStep : IAdjustmentStep
{
    public void Apply(Image<Rgba32> image, AdjustmentParams parameters)
    {
        if (parameters.Blur <= 0.5f) return;

        float sigma = parameters.Blur / 100f * 5f + 0.2f;
        image.Mutate(ctx => ctx.GaussianBlur(sigma));
    }
}
