using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VRCosme.Pipeline;

namespace VRCosme.Services.ImageProcessing;

public enum ExportFormat { Png, Jpeg }

/// <summary>全補正パラメータをまとめた構造体</summary>
public record AdjustmentParams(
    float Brightness,    // 乗数: 0.0–2.0 (1.0 = 変化なし)
    float Contrast,      // 乗数: 0.0–2.0 (1.0 = 変化なし)
    float Gamma,         // 0.2–5.0 (1.0 = 変化なし)
    float Exposure,      // EV: -5.0–5.0 (0.0 = 変化なし)
    float Saturation,    // 乗数: 0.0–2.0 (1.0 = 変化なし)
    float Temperature,   // -100–100 (0 = 変化なし)
    float Tint,          // -100–100 (0 = 変化なし)
    float Shadows,       // -100–100 (0 = 変化なし)
    float Highlights,    // -100–100 (0 = 変化なし)
    float Clarity,       // -100–100 (0 = 変化なし)
    float Sharpen,       // 0–100 (0 = 変化なし)
    float Vignette,      // -100–100 (0 = 変化なし)
    float Blur = 0f      // 0–100 (0 = 変化なし)
)
{
    public static AdjustmentParams Default { get; } =
        new(1f, 1f, 1f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);

    public bool IsDefault =>
        MathF.Abs(Brightness - 1f) < 0.001f && MathF.Abs(Contrast - 1f) < 0.001f &&
        MathF.Abs(Gamma - 1f) < 0.01f && MathF.Abs(Exposure) < 0.01f &&
        MathF.Abs(Saturation - 1f) < 0.001f && MathF.Abs(Temperature) < 0.01f &&
        MathF.Abs(Tint) < 0.01f && MathF.Abs(Shadows) < 0.01f &&
        MathF.Abs(Highlights) < 0.01f && MathF.Abs(Clarity) < 0.01f &&
        Sharpen < 0.01f && MathF.Abs(Vignette) < 0.01f &&
        Blur < 0.01f;
}

public static class ImageProcessor
{
    private static readonly PipelineRenderer PreviewPipeline = new();

    // ───────── 読み込み・プレビュー ─────────

    public static Image<Rgba32> LoadImage(string path) => Image.Load<Rgba32>(path);

    public static Image<Rgba32> CreatePreview(Image<Rgba32> source, int maxDimension = 1920)
    {
        if (source.Width <= maxDimension && source.Height <= maxDimension)
            return source.Clone();

        var clone = source.Clone();
        clone.Mutate(x => x.Resize(new ResizeOptions
        {
            Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max,
            Size = new SixLabors.ImageSharp.Size(maxDimension, maxDimension)
        }));
        return clone;
    }

    // ───────── 回転・反転 ─────────

    public static Image<Rgba32> ApplyTransform(Image<Rgba32> source, int rotationDeg, bool flipH, bool flipV)
    {
        var result = source.Clone();
        result.Mutate(ctx =>
        {
            if (rotationDeg != 0) ctx.Rotate(rotationDeg);
            if (flipH) ctx.Flip(FlipMode.Horizontal);
            if (flipV) ctx.Flip(FlipMode.Vertical);
        });
        return result;
    }

    // ───────── 補正適用 ─────────

    public static Image<Rgba32> ApplyAdjustments(
        Image<Rgba32> source,
        AdjustmentParams p,
        byte[]? mask = null,
        bool naturalizeBoundary = false)
    {
        var adjusted = PreviewPipeline.RenderPreview(source, p);
        if (mask == null || mask.Length != source.Width * source.Height)
            return adjusted;

        BlendByMask(source, adjusted, mask, naturalizeBoundary);
        return adjusted;
    }

    // ───────── 変換・書き出し ─────────

    public static BitmapSource ToBitmapSource(Image<Rgba32> image)
    {
        int width = image.Width, height = image.Height;
        byte[] data = new byte[width * height * 4];
        image.CopyPixelDataTo(data);

        // RGBA → BGRA
        for (int i = 0; i < data.Length; i += 4)
            (data[i], data[i + 2]) = (data[i + 2], data[i]);

        var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, width, height), data, width * 4, 0);
        wb.Freeze();
        return wb;
    }

    public static BitmapSource? CreateMaskOverlayBitmap(byte[]? mask, int width, int height)
    {
        if (mask == null || width <= 0 || height <= 0 || mask.Length != width * height)
            return null;

        byte[] data = new byte[width * height * 4];
        for (int i = 0, p = 0; i < mask.Length; i++, p += 4)
        {
            byte alpha = mask[i];
            if (alpha == 0) continue;

            data[p] = 50;       // B
            data[p + 1] = 120;  // G
            data[p + 2] = 255;  // R
            data[p + 3] = alpha;
        }

        var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, width, height), data, width * 4, 0);
        wb.Freeze();
        return wb;
    }

    public static Image<Rgba32> Crop(Image<Rgba32> source, SixLabors.ImageSharp.Rectangle rect)
    {
        var result = source.Clone();
        result.Mutate(x => x.Crop(rect));
        return result;
    }

    public static void Export(Image<Rgba32> image, string path, ExportFormat format, int jpegQuality = 90)
    {
        switch (format)
        {
            case ExportFormat.Png:
                image.Save(path, new PngEncoder());
                break;
            case ExportFormat.Jpeg:
                image.Save(path, new JpegEncoder { Quality = jpegQuality });
                break;
        }
    }

    private static void BlendByMask(Image<Rgba32> original, Image<Rgba32> adjusted, byte[] mask, bool naturalizeBoundary)
    {
        int width = adjusted.Width;
        int height = adjusted.Height;
        int pixelCount = width * height;
        if (mask.Length != pixelCount)
            return;

        var effectiveMask = naturalizeBoundary
            ? CreateBoundaryNaturalizedMask(mask, width, height)
            : mask;

        var src = new byte[pixelCount * 4];
        var dst = new byte[pixelCount * 4];
        original.CopyPixelDataTo(src);
        adjusted.CopyPixelDataTo(dst);

        for (int i = 0; i < pixelCount; i++)
        {
            byte m = effectiveMask[i];
            int p = i * 4;
            if (m == 255) continue;
            if (m == 0)
            {
                dst[p] = src[p];
                dst[p + 1] = src[p + 1];
                dst[p + 2] = src[p + 2];
                dst[p + 3] = src[p + 3];
                continue;
            }

            float t = m / 255f;
            dst[p] = (byte)Math.Clamp(src[p] + (dst[p] - src[p]) * t, 0f, 255f);
            dst[p + 1] = (byte)Math.Clamp(src[p + 1] + (dst[p + 1] - src[p + 1]) * t, 0f, 255f);
            dst[p + 2] = (byte)Math.Clamp(src[p + 2] + (dst[p + 2] - src[p + 2]) * t, 0f, 255f);
            dst[p + 3] = (byte)Math.Clamp(src[p + 3] + (dst[p + 3] - src[p + 3]) * t, 0f, 255f);
        }

        adjusted.ProcessPixelRows(accessor =>
        {
            int offset = 0;
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgba32(dst[offset], dst[offset + 1], dst[offset + 2], dst[offset + 3]);
                    offset += 4;
                }
            }
        });
    }

    private static byte[] CreateBoundaryNaturalizedMask(byte[] sourceMask, int width, int height)
    {
        // 強めの境界自然化: 距離ベースで段階減衰 + ぼかしで階段境界を緩和
        const int featherRadius = 10;
        const float featherGamma = 1.75f;
        const int blurRadius = 2;
        const int blurPasses = 2;

        var softened = (byte[])sourceMask.Clone();
        if (featherRadius <= 0 || width <= 1 || height <= 1 || sourceMask.Length == 0)
            return softened;

        int pixelCount = width * height;
        var distances = new int[pixelCount];
        Array.Fill(distances, -1);

        var queue = new Queue<int>(Math.Min(pixelCount, 8192));
        for (int i = 0; i < pixelCount; i++)
        {
            if (sourceMask[i] != 0)
                continue;

            distances[i] = 0;
            queue.Enqueue(i);
        }

        if (queue.Count == 0)
            return softened;

        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int baseDistance = distances[idx];
            if (baseDistance >= featherRadius + blurRadius * blurPasses + 1)
                continue;

            int x = idx % width;
            int y = idx / width;

            for (int oy = -1; oy <= 1; oy++)
            {
                int ny = y + oy;
                if ((uint)ny >= (uint)height)
                    continue;

                int row = ny * width;
                for (int ox = -1; ox <= 1; ox++)
                {
                    if (ox == 0 && oy == 0)
                        continue;

                    int nx = x + ox;
                    if ((uint)nx >= (uint)width)
                        continue;

                    int next = row + nx;
                    if (distances[next] >= 0)
                        continue;

                    distances[next] = baseDistance + 1;
                    queue.Enqueue(next);
                }
            }
        }

        for (int i = 0; i < pixelCount; i++)
        {
            byte current = sourceMask[i];
            if (current == 0)
                continue;

            int distance = distances[i];
            if (distance < 0)
                continue;

            if (distance >= featherRadius)
            {
                softened[i] = current;
                continue;
            }

            float t = Math.Clamp(distance / (float)featherRadius, 0f, 1f);
            float smooth = t * t * (3f - 2f * t); // smoothstep
            float factor = MathF.Pow(smooth, featherGamma);
            softened[i] = (byte)Math.Clamp(MathF.Round(current * factor), 0f, 255f);
        }

        var blurred = softened;
        for (int i = 0; i < blurPasses; i++)
            blurred = BoxBlurMask(blurred, width, height, blurRadius);

        int preserveThreshold = featherRadius + blurRadius * blurPasses;
        for (int i = 0; i < pixelCount; i++)
        {
            if (sourceMask[i] == 255 && distances[i] >= preserveThreshold)
                blurred[i] = 255;
        }

        return blurred;
    }

    private static byte[] BoxBlurMask(byte[] source, int width, int height, int radius)
    {
        if (radius <= 0 || source.Length == 0 || width <= 0 || height <= 0)
            return (byte[])source.Clone();

        var horizontal = new byte[source.Length];
        var output = new byte[source.Length];

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int sum = 0;
            int leftBound = 0;
            int rightBound = Math.Min(width - 1, radius);
            for (int x = leftBound; x <= rightBound; x++)
                sum += source[row + x];

            for (int x = 0; x < width; x++)
            {
                int left = Math.Max(0, x - radius);
                int right = Math.Min(width - 1, x + radius);
                int count = right - left + 1;
                horizontal[row + x] = (byte)(sum / count);

                int removeIdx = x - radius;
                int addIdx = x + radius + 1;
                if (removeIdx >= 0)
                    sum -= source[row + removeIdx];
                if (addIdx < width)
                    sum += source[row + addIdx];
            }
        }

        for (int x = 0; x < width; x++)
        {
            int sum = 0;
            int topBound = 0;
            int bottomBound = Math.Min(height - 1, radius);
            for (int y = topBound; y <= bottomBound; y++)
                sum += horizontal[y * width + x];

            for (int y = 0; y < height; y++)
            {
                int top = Math.Max(0, y - radius);
                int bottom = Math.Min(height - 1, y + radius);
                int count = bottom - top + 1;
                output[y * width + x] = (byte)(sum / count);

                int removeY = y - radius;
                int addY = y + radius + 1;
                if (removeY >= 0)
                    sum -= horizontal[removeY * width + x];
                if (addY < height)
                    sum += horizontal[addY * width + x];
            }
        }

        return output;
    }
}
