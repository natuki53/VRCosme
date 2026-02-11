using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VRCosme.Services.ImageProcessing;

namespace VRCosme.Pipeline;

internal sealed class ClarityStep : IAdjustmentStep
{
    public void Apply(Image<Rgba32> image, AdjustmentParams parameters)
    {
        if (MathF.Abs(parameters.Clarity) <= 0.5f) return;
        ApplyClarity(image, parameters.Clarity / 100f);
    }

    private static void ApplyClarity(Image<Rgba32> image, float amount)
    {
        using var blurred = image.Clone();
        float blurRadius = Math.Max(2f, image.Width / 120f);
        blurred.Mutate(ctx => ctx.GaussianBlur(blurRadius));

        byte[] orig = new byte[image.Width * image.Height * 4];
        byte[] blur = new byte[orig.Length];
        image.CopyPixelDataTo(orig);
        blurred.CopyPixelDataTo(blur);

        for (int i = 0; i < orig.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                int idx = i + c;
                int diff = orig[idx] - blur[idx];
                orig[idx] = (byte)Math.Clamp(orig[idx] + (int)(diff * amount), 0, 255);
            }
        }

        image.ProcessPixelRows(accessor =>
        {
            int offset = 0;
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgba32(orig[offset], orig[offset + 1], orig[offset + 2], orig[offset + 3]);
                    offset += 4;
                }
            }
        });
    }
}
