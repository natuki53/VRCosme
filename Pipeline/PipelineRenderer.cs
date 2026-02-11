using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using VRCosme.Services.ImageProcessing;

namespace VRCosme.Pipeline;

public sealed class PipelineRenderer
{
    private readonly IReadOnlyList<IAdjustmentStep> _steps =
    [
        new PixelAdjustmentsStep(),
        new SaturationStep(),
        new ClarityStep(),
        new SharpenStep(),
    ];

    public Image<Rgba32> RenderPreview(Image<Rgba32> source, AdjustmentParams parameters)
    {
        if (parameters.IsDefault) return source.Clone();

        var result = source.Clone();
        foreach (var step in _steps)
            step.Apply(result, parameters);

        return result;
    }
}
