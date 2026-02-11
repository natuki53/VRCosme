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

    public static Image<Rgba32> ApplyAdjustments(Image<Rgba32> source, AdjustmentParams p, byte[]? mask = null)
    {
        var adjusted = PreviewPipeline.RenderPreview(source, p);
        if (mask == null || mask.Length != source.Width * source.Height)
            return adjusted;

        BlendByMask(source, adjusted, mask);
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

    private static void BlendByMask(Image<Rgba32> original, Image<Rgba32> adjusted, byte[] mask)
    {
        int width = adjusted.Width;
        int height = adjusted.Height;
        int pixelCount = width * height;
        if (mask.Length != pixelCount)
            return;

        var src = new byte[pixelCount * 4];
        var dst = new byte[pixelCount * 4];
        original.CopyPixelDataTo(src);
        adjusted.CopyPixelDataTo(dst);

        for (int i = 0; i < pixelCount; i++)
        {
            byte m = mask[i];
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
}
