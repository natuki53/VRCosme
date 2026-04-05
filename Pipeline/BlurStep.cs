using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VRCosme.Services.ImageProcessing;

namespace VRCosme.Pipeline;

internal sealed class BlurStep : IAdjustmentStep
{
    private const float MaxBlurValue = 300f;
    private const float MaxSigma = 15f;

    public void Apply(Image<Rgba32> image, AdjustmentParams parameters)
    {
        if (parameters.Blur <= 0.5f) return;

        float blur = Math.Clamp(parameters.Blur, 0f, MaxBlurValue);
        float sigma = blur / MaxBlurValue * MaxSigma + 0.2f;
        image.Mutate(ctx => ctx.GaussianBlur(sigma));
    }
}
