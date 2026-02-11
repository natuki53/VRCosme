using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using VRCosme.Services.ImageProcessing;

namespace VRCosme.Pipeline;

internal sealed class PixelAdjustmentsStep : IAdjustmentStep
{
    public void Apply(Image<Rgba32> image, AdjustmentParams parameters)
    {
        bool needPixelPass =
            MathF.Abs(parameters.Exposure) > 0.01f || MathF.Abs(parameters.Temperature) > 0.01f ||
            MathF.Abs(parameters.Tint) > 0.01f || MathF.Abs(parameters.Brightness - 1f) > 0.001f ||
            MathF.Abs(parameters.Contrast - 1f) > 0.001f || MathF.Abs(parameters.Shadows) > 0.01f ||
            MathF.Abs(parameters.Highlights) > 0.01f || MathF.Abs(parameters.Gamma - 1f) > 0.01f ||
            MathF.Abs(parameters.Vignette) > 0.01f;

        if (!needPixelPass) return;

        int w = image.Width, h = image.Height;
        float cx = w / 2f, cy = h / 2f;
        float maxDist = MathF.Sqrt(cx * cx + cy * cy);
        float exposureMul = MathF.Pow(2f, parameters.Exposure);
        float tempShift = parameters.Temperature / 100f * 0.15f;
        float tintShift = parameters.Tint / 100f * 0.10f;
        float shadowAmt = parameters.Shadows / 100f * 0.5f;
        float highlightAmt = parameters.Highlights / 100f * 0.5f;
        float invGamma = 1f / parameters.Gamma;
        float vignetteAmt = parameters.Vignette / 100f;
        bool doGamma = MathF.Abs(invGamma - 1f) > 0.01f;
        bool doVignette = MathF.Abs(vignetteAmt) > 0.01f;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref var px = ref row[x];
                    float r = px.R / 255f, g = px.G / 255f, b = px.B / 255f;

                    // 露出
                    r *= exposureMul; g *= exposureMul; b *= exposureMul;

                    // 色温度
                    r += tempShift; b -= tempShift;

                    // 色かぶり
                    g += tintShift;

                    // 明るさ
                    r *= parameters.Brightness; g *= parameters.Brightness; b *= parameters.Brightness;

                    // コントラスト
                    r = (r - 0.5f) * parameters.Contrast + 0.5f;
                    g = (g - 0.5f) * parameters.Contrast + 0.5f;
                    b = (b - 0.5f) * parameters.Contrast + 0.5f;

                    // シャドウ・ハイライト
                    float lum = Math.Clamp(r * 0.2126f + g * 0.7152f + b * 0.0722f, 0f, 1f);
                    float shadowW = (1f - lum) * (1f - lum);
                    float highlightW = lum * lum;
                    float tonalAdj = shadowAmt * shadowW + highlightAmt * highlightW;
                    r += tonalAdj; g += tonalAdj; b += tonalAdj;

                    // ガンマ
                    if (doGamma)
                    {
                        r = MathF.Pow(Math.Clamp(r, 0f, 1f), invGamma);
                        g = MathF.Pow(Math.Clamp(g, 0f, 1f), invGamma);
                        b = MathF.Pow(Math.Clamp(b, 0f, 1f), invGamma);
                    }

                    // 周辺減光
                    if (doVignette)
                    {
                        float dx = x - cx, dy = y - cy;
                        float dist = MathF.Sqrt(dx * dx + dy * dy) / maxDist;
                        float vf = Math.Max(0f, 1f - vignetteAmt * dist * dist);
                        r *= vf; g *= vf; b *= vf;
                    }

                    px.R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
                    px.G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
                    px.B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
                }
            }
        });
    }
}
