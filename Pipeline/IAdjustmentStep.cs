using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using VRCosme.Services.ImageProcessing;

namespace VRCosme.Pipeline;

internal interface IAdjustmentStep
{
    void Apply(Image<Rgba32> image, AdjustmentParams parameters);
}
