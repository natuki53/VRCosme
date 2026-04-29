using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Processing;
using System.IO;
using VRCosme.Models;
using VRCosme.Services.ImageProcessing;

namespace VRCosme.Services.AI;

/// <summary>
/// SAM (Segment Anything Model) を使ったクリック座標ベースのマスク生成サービス。
/// 画像のエンコーディング結果をバージョンキーでキャッシュし、
/// 同一画像への連続クリックは高速なデコード処理のみで完結します。
/// </summary>
public sealed class SamSelectorService : IDisposable
{
    private const int SamSize = 1024;

    // SAM 標準の正規化パラメータ (ImageNet、ピクセル値 0-255 スケール)
    private const float MeanR = 123.675f;
    private const float MeanG = 116.28f;
    private const float MeanB = 103.53f;
    private const float StdR = 58.395f;
    private const float StdG = 57.12f;
    private const float StdB = 57.375f;

    private readonly object _sync = new();

    private InferenceSession? _encoderSession;
    private InferenceSession? _decoderSession;
    private string? _encoderPath;
    private string? _decoderPath;
    private AutoMaskExecutionDevice _executionDevice;

    // エンコーディングキャッシュ
    private float[]? _cachedEmbeddings;
    private int _cachedImageVersion = -1;
    private float _cachedScale;

    public byte[] CreateMask(
        Image<Rgba32> source,
        int imageVersion,
        string encoderPath,
        string decoderPath,
        int clickX,
        int clickY,
        AutoMaskExecutionDevice executionDevice)
    {
        if (source.Width <= 0 || source.Height <= 0)
            throw new ArgumentException("Invalid source image size.", nameof(source));
        if (!File.Exists(encoderPath))
            throw new FileNotFoundException("SAM encoder model not found.", encoderPath);
        if (!File.Exists(decoderPath))
            throw new FileNotFoundException("SAM decoder model not found.", decoderPath);

        lock (_sync)
        {
            EnsureSessions(encoderPath, decoderPath, executionDevice);

            // キャッシュミス時のみエンコード (画像変更・モデル変更時)
            if (_cachedEmbeddings == null || _cachedImageVersion != imageVersion)
            {
                LogService.Info($"SAM: 画像をエンコード中 (version={imageVersion}, size={source.Width}x{source.Height})");
                (_cachedEmbeddings, _cachedScale) = EncodeImage(source);
                _cachedImageVersion = imageVersion;
                LogService.Info("SAM: エンコード完了");
            }

            return Decode(
                _cachedEmbeddings,
                _cachedScale,
                source,
                source.Width,
                source.Height,
                Math.Clamp(clickX, 0, source.Width - 1),
                Math.Clamp(clickY, 0, source.Height - 1));
        }
    }

    /// <summary>画像バージョンが変わったときにキャッシュを破棄します。</summary>
    public void InvalidateCache()
    {
        lock (_sync)
        {
            _cachedEmbeddings = null;
            _cachedImageVersion = -1;
        }
    }

    // ─────────────────────────────────────────────────────
    //  セッション管理
    // ─────────────────────────────────────────────────────

    private void EnsureSessions(
        string encoderPath,
        string decoderPath,
        AutoMaskExecutionDevice executionDevice)
    {
        bool encoderChanged = !string.Equals(_encoderPath, encoderPath, StringComparison.OrdinalIgnoreCase);
        bool decoderChanged = !string.Equals(_decoderPath, decoderPath, StringComparison.OrdinalIgnoreCase);
        bool deviceChanged = _executionDevice != executionDevice;

        if (_encoderSession != null && _decoderSession != null
            && !encoderChanged && !decoderChanged && !deviceChanged)
            return;

        _encoderSession?.Dispose();
        _decoderSession?.Dispose();

        _encoderSession = CreateSession(encoderPath, executionDevice, "エンコーダー");
        _decoderSession = CreateSession(decoderPath, executionDevice, "デコーダー");
        _encoderPath = encoderPath;
        _decoderPath = decoderPath;
        _executionDevice = executionDevice;

        // セッション変更時はキャッシュも無効化
        _cachedEmbeddings = null;
        _cachedImageVersion = -1;
    }

    private static InferenceSession CreateSession(
        string modelPath,
        AutoMaskExecutionDevice executionDevice,
        string label)
    {
        if (executionDevice == AutoMaskExecutionDevice.Gpu)
        {
            try
            {
                var gpuOptions = CreateDefaultOptions();
                gpuOptions.AppendExecutionProvider_DML(0);
                var session = new InferenceSession(modelPath, gpuOptions);
                LogService.Info($"SAM {label} 推論デバイス: GPU (DirectML)");
                return session;
            }
            catch (Exception ex)
            {
                LogService.Error($"SAM {label} GPU セッション作成に失敗。CPU にフォールバックします。", ex);
            }
        }

        var cpuOptions = CreateDefaultOptions();
        var cpuSession = new InferenceSession(modelPath, cpuOptions);
        LogService.Info($"SAM {label} 推論デバイス: CPU");
        return cpuSession;
    }

    private static SessionOptions CreateDefaultOptions() =>
        new() { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };

    // ─────────────────────────────────────────────────────
    //  エンコーダー
    // ─────────────────────────────────────────────────────

    private (float[] embeddings, float scale) EncodeImage(Image<Rgba32> source)
    {
        float scale = SamSize / (float)Math.Max(source.Width, source.Height);
        int newW = (int)Math.Round(source.Width * scale);
        int newH = (int)Math.Round(source.Height * scale);

        var tensor = BuildEncoderTensor(source, newW, newH);

        string inputName = _encoderSession!.InputMetadata.First().Key;
        var input = NamedOnnxValue.CreateFromTensor(inputName, tensor);

        using var results = _encoderSession.Run([input]);
        var outputTensor = results.First().AsTensor<float>()
            ?? throw new InvalidOperationException("SAM エンコーダーの出力が float テンソルではありません。");

        return (outputTensor.ToArray(), scale);
    }

    private static DenseTensor<float> BuildEncoderTensor(Image<Rgba32> source, int newW, int newH)
    {
        var tensor = new DenseTensor<float>([1, 3, SamSize, SamSize]);

        // パディング領域を正規化ゼロ (黒ピクセル) で初期化
        float padR = -MeanR / StdR;
        float padG = -MeanG / StdG;
        float padB = -MeanB / StdB;
        int plane = SamSize * SamSize;
        tensor.Buffer.Span[..(plane)].Fill(padR);
        tensor.Buffer.Span[plane..(2 * plane)].Fill(padG);
        tensor.Buffer.Span[(2 * plane)..(3 * plane)].Fill(padB);

        using var resized = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(newW, newH),
            Sampler = KnownResamplers.Bicubic,
            Mode = ResizeMode.Stretch
        }));

        for (int y = 0; y < newH; y++)
        {
            var row = resized.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < newW; x++)
            {
                var px = row[x];
                tensor[0, 0, y, x] = (px.R - MeanR) / StdR;
                tensor[0, 1, y, x] = (px.G - MeanG) / StdG;
                tensor[0, 2, y, x] = (px.B - MeanB) / StdB;
            }
        }

        return tensor;
    }

    // ─────────────────────────────────────────────────────
    //  デコーダー
    // ─────────────────────────────────────────────────────

    private byte[] Decode(
        float[] embeddings,
        float scale,
        Image<Rgba32> source,
        int origW,
        int origH,
        int clickX,
        int clickY)
    {
        // クリック座標を SAM 入力空間 (1024x1024) にスケール変換
        float xSam = clickX * scale;
        float ySam = clickY * scale;

        // image_embeddings: [1, 256, 64, 64]
        var embTensor = new DenseTensor<float>(embeddings, [1, 256, 64, 64]);

        // point_coords: [1, N, 2]
        // 一部モデルは N=5 を要求するため、入力メタデータから必要数を取得する。
        int pointCount = GetPointCountFromDecoderInputMetadata();
        var pointCoords = new DenseTensor<float>([1, pointCount, 2]);
        pointCoords[0, 0, 0] = xSam;
        pointCoords[0, 0, 1] = ySam;

        // point_labels: [1, N]  (1=前景, -1=パディング)
        var pointLabels = new DenseTensor<float>([1, pointCount]);
        pointLabels[0, 0] = 1f;
        float paddingLabel = pointCount >= 5 ? 0f : -1f;
        for (int i = 1; i < pointCount; i++)
            pointLabels[0, i] = paddingLabel;

        // mask_input: [1, 1, 256, 256]  (前回マスクなし → ゼロ)
        var maskInput = new DenseTensor<float>([1, 1, 256, 256]);

        // has_mask_input: [1]
        var hasMaskInput = new DenseTensor<float>([1]);
        hasMaskInput[0] = 0f;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image_embeddings", embTensor),
            NamedOnnxValue.CreateFromTensor("point_coords", pointCoords),
            NamedOnnxValue.CreateFromTensor("point_labels", pointLabels),
            NamedOnnxValue.CreateFromTensor("mask_input", maskInput),
            NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInput),
        };
        
        // モデルによっては orig_im_size 入力を持たない。
        if (DecoderHasInput("orig_im_size"))
        {
            var origImSize = new DenseTensor<float>([2]);
            origImSize[0] = origH;
            origImSize[1] = origW;
            inputs.Add(NamedOnnxValue.CreateFromTensor("orig_im_size", origImSize));
        }

        using var results = _decoderSession!.Run(inputs);
        var mask = ExtractBestMask(results, origW, origH, clickX, clickY);
        mask = KeepConnectedComponentContainingClick(mask, origW, origH, clickX, clickY);
        mask = RefineMaskWithColorGuidance(source, mask, origW, origH, clickX, clickY);
        mask = KeepConnectedComponentContainingClick(mask, origW, origH, clickX, clickY);
        FillHoles(mask, origW, origH);
        return mask;
    }

    private static byte[] ExtractBestMask(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        int origW,
        int origH,
        int clickX,
        int clickY)
    {
        float[]? iouPredictions = null;
        float[]? fullMasksLogits = null;
        int fullMaskH = 0, fullMaskW = 0;
        float[]? lowResMasksLogits = null;
        int lowResMaskH = 0, lowResMaskW = 0;

        foreach (var output in results)
        {
            var tensor = output.AsTensor<float>();
            if (tensor == null) continue;

            if (output.Name == "iou_predictions")
            {
                iouPredictions = tensor.ToArray();
            }
            else if (output.Name == "masks")
            {
                var dims = tensor.Dimensions.ToArray();
                if (dims.Length >= 4)
                {
                    fullMaskH = dims[^2];
                    fullMaskW = dims[^1];
                    fullMasksLogits = tensor.ToArray();
                }
            }
            else if (output.Name == "low_res_masks")
            {
                var dims = tensor.Dimensions.ToArray();
                if (dims.Length >= 4)
                {
                    lowResMaskH = dims[^2];
                    lowResMaskW = dims[^1];
                    lowResMasksLogits = tensor.ToArray();
                }
            }
        }

        // 高品質優先: `masks` がある場合は必ずそちらを採用し、
        // `low_res_masks` はフォールバックでのみ使う。
        float[]? masksLogits = fullMasksLogits;
        int maskH = fullMaskH;
        int maskW = fullMaskW;
        bool usingLowResMasks = false;
        if (masksLogits == null || maskH <= 0 || maskW <= 0)
        {
            masksLogits = lowResMasksLogits;
            maskH = lowResMaskH;
            maskW = lowResMaskW;
            usingLowResMasks = true;
        }

        if (masksLogits == null || maskH <= 0 || maskW <= 0)
            throw new InvalidOperationException("SAM デコーダーの出力マスクが取得できませんでした。");

        // Photoshop のオブジェクト選択に寄せるため、クリック点を含む候補を優先する。
        int numMasks = masksLogits.Length / (maskH * maskW);
        int plane = maskH * maskW;
        int bestIdx = 0;
        float bestScore = float.MinValue;
        int samSide = Math.Max(origW, origH);
        int mappedClickX = (maskW == origW && maskH == origH)
            ? MapCoordinate(clickX, origW, maskW)
            : MapCoordinate(clickX, samSide, maskW);
        int mappedClickY = (maskW == origW && maskH == origH)
            ? MapCoordinate(clickY, origH, maskH)
            : MapCoordinate(clickY, samSide, maskH);

        for (int i = 0; i < numMasks; i++)
        {
            int offset = i * plane;
            bool containsClick = masksLogits[offset + (mappedClickY * maskW) + mappedClickX] > 0f;

            int area = 0;
            for (int p = 0; p < plane; p++)
            {
                if (masksLogits[offset + p] > 0f)
                    area++;
            }

            float areaRatio = area / (float)plane;
            float iou = (iouPredictions != null && i < iouPredictions.Length)
                ? iouPredictions[i]
                : 0f;

            float score = iou;
            if (containsClick)
                score += 2.0f;

            if (areaRatio > 0.90f)
                score -= 2.0f;
            else if (areaRatio > 0.75f)
                score -= 0.6f;
            else if (areaRatio > 0.50f)
                score -= 0.2f;

            if (areaRatio < 0.0005f)
                score -= 1.0f;

            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }

        var logits = new float[plane];
        Array.Copy(masksLogits, bestIdx * plane, logits, 0, plane);

        // ピクセル精度改善: 低解像度マスクはロジットを双一次補間してから2値化する。
        // 先に2値化して最近傍拡大すると境界が階段状になりやすい。
        byte[] restored = (maskW == origW && maskH == origH)
            ? BinarizeLogits(logits)
            : usingLowResMasks
                ? BinarizeLogits(RestoreLogitsFromSamSquare(logits, maskW, maskH, origW, origH))
                : BinarizeLogits(ResizeLogitsBilinear(logits, maskW, maskH, origW, origH));

        return restored;
    }

    private int GetPointCountFromDecoderInputMetadata()
    {
        if (_decoderSession == null)
            return 2;
        if (!_decoderSession.InputMetadata.TryGetValue("point_coords", out var pointMeta))
            return 2;

        var dims = pointMeta.Dimensions;
        if (dims.Length < 2)
            return 2;

        int count = dims[1];
        if (count <= 0)
            return 2;
        return count;
    }

    private bool DecoderHasInput(string inputName) =>
        _decoderSession != null && _decoderSession.InputMetadata.ContainsKey(inputName);

    private static int MapCoordinate(int coord, int srcSize, int dstSize)
    {
        if (dstSize <= 1 || srcSize <= 1)
            return 0;
        double t = coord / (double)(srcSize - 1);
        int mapped = (int)Math.Round(t * (dstSize - 1));
        return Math.Clamp(mapped, 0, dstSize - 1);
    }

    private static byte[] BinarizeLogits(float[] logits)
    {
        var binary = new byte[logits.Length];
        for (int i = 0; i < logits.Length; i++)
            binary[i] = logits[i] > 0f ? (byte)255 : (byte)0;
        return binary;
    }

    private static float[] RestoreLogitsFromSamSquare(
        float[] src,
        int srcW,
        int srcH,
        int origW,
        int origH)
    {
        int side = Math.Max(origW, origH);
        var square = ResizeLogitsBilinear(src, srcW, srcH, side, side);
        var dst = new float[origW * origH];
        for (int y = 0; y < origH; y++)
        {
            Array.Copy(square, y * side, dst, y * origW, origW);
        }

        return dst;
    }

    private static float[] ResizeLogitsBilinear(float[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new float[dstW * dstH];
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
            return dst;

        double scaleX = srcW / (double)dstW;
        double scaleY = srcH / (double)dstH;
        for (int y = 0; y < dstH; y++)
        {
            double sy = ((y + 0.5) * scaleY) - 0.5;
            int y0 = (int)Math.Floor(sy);
            int y1 = y0 + 1;
            double wy = sy - y0;
            if (y0 < 0) { y0 = 0; wy = 0; }
            if (y1 >= srcH) y1 = srcH - 1;

            int dstRow = y * dstW;
            int srcRow0 = y0 * srcW;
            int srcRow1 = y1 * srcW;
            for (int x = 0; x < dstW; x++)
            {
                double sx = ((x + 0.5) * scaleX) - 0.5;
                int x0 = (int)Math.Floor(sx);
                int x1 = x0 + 1;
                double wx = sx - x0;
                if (x0 < 0) { x0 = 0; wx = 0; }
                if (x1 >= srcW) x1 = srcW - 1;

                float v00 = src[srcRow0 + x0];
                float v10 = src[srcRow0 + x1];
                float v01 = src[srcRow1 + x0];
                float v11 = src[srcRow1 + x1];
                float top = (float)(v00 + ((v10 - v00) * wx));
                float bottom = (float)(v01 + ((v11 - v01) * wx));
                dst[dstRow + x] = (float)(top + ((bottom - top) * wy));
            }
        }

        return dst;
    }

    private static byte[] RefineMaskWithColorGuidance(
        Image<Rgba32> source,
        byte[] baseMask,
        int width,
        int height,
        int clickX,
        int clickY)
    {
        if (baseMask.Length != width * height || width <= 0 || height <= 0)
            return baseMask;

        var connected = KeepConnectedComponentContainingClick(baseMask, width, height, clickX, clickY);
        int connectedCount = CountMaskPixels(connected);
        if (connectedCount == 0)
            return connected;

        int colorError = EstimateColorError(source, width, height, clickX, clickY);
        var colorMask = MaskProcessingService.BuildColorSelectionMask(
            source, clickX, clickY, colorError, connectivity: 8, gapClosing: 2, antialias: false);

        var expanded = MaskProcessingService.BuildDilatedBinaryMask(connected, width, height, iterations: 4);
        var interior = ErodeBinary(connected, width, height, iterations: 2);
        var refined = new byte[connected.Length];

        for (int i = 0; i < refined.Length; i++)
        {
            if (interior[i] != 0)
            {
                refined[i] = 255;
                continue;
            }

            if (expanded[i] == 0)
            {
                refined[i] = 0;
                continue;
            }

            refined[i] = colorMask[i] != 0 ? (byte)255 : (byte)0;
        }

        // 細い欠け/隙間を埋める。
        var closed = MaskProcessingService.BuildDilatedBinaryMask(refined, width, height, iterations: 1);
        refined = ErodeBinary(closed, width, height, iterations: 1);
        FillHoles(refined, width, height);

        int refinedCount = CountMaskPixels(refined);
        if (refinedCount == 0 || refinedCount < connectedCount * 0.35f || refinedCount > connectedCount * 1.9f)
            return connected;

        return refined;
    }

    private static int EstimateColorError(
        Image<Rgba32> source,
        int width,
        int height,
        int clickX,
        int clickY)
    {
        int seedX = Math.Clamp(clickX, 0, width - 1);
        int seedY = Math.Clamp(clickY, 0, height - 1);
        var seed = source[seedX, seedY];

        int minX = Math.Max(0, seedX - 4);
        int maxX = Math.Min(width - 1, seedX + 4);
        int minY = Math.Max(0, seedY - 4);
        int maxY = Math.Min(height - 1, seedY + 4);
        double sumDiff = 0.0;
        int count = 0;
        for (int y = minY; y <= maxY; y++)
        {
            var row = source.DangerousGetPixelRowMemory(y).Span;
            for (int x = minX; x <= maxX; x++)
            {
                var px = row[x];
                int diff = (Math.Abs(px.R - seed.R) + Math.Abs(px.G - seed.G) + Math.Abs(px.B - seed.B)) / 3;
                sumDiff += diff;
                count++;
            }
        }

        if (count <= 0)
            return 18;

        int estimated = (int)Math.Round((sumDiff / count) * 1.65 + 10.0);
        return Math.Clamp(estimated, 12, 42);
    }

    private static byte[] ErodeBinary(byte[] source, int width, int height, int iterations)
    {
        iterations = Math.Max(0, iterations);
        var current = (byte[])source.Clone();
        var next = new byte[source.Length];

        for (int iter = 0; iter < iterations; iter++)
        {
            Array.Clear(next, 0, next.Length);
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = row + x;
                    if (current[idx] == 0)
                        continue;

                    bool keep = true;
                    for (int oy = -1; oy <= 1 && keep; oy++)
                    {
                        int ny = y + oy;
                        if ((uint)ny >= (uint)height)
                        {
                            keep = false;
                            break;
                        }

                        int nrow = ny * width;
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int nx = x + ox;
                            if ((uint)nx >= (uint)width || current[nrow + nx] == 0)
                            {
                                keep = false;
                                break;
                            }
                        }
                    }

                    if (keep)
                        next[idx] = 255;
                }
            }

            (current, next) = (next, current);
        }

        return current;
    }

    private static void FillHoles(byte[] mask, int width, int height)
    {
        int pixelCount = width * height;
        var external = new bool[pixelCount];
        var queue = new int[pixelCount];
        int head = 0, tail = 0;

        void EnqueueIfBackground(int x, int y)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
                return;
            int idx = y * width + x;
            if (external[idx] || mask[idx] != 0)
                return;
            external[idx] = true;
            queue[tail++] = idx;
        }

        for (int x = 0; x < width; x++)
        {
            EnqueueIfBackground(x, 0);
            EnqueueIfBackground(x, height - 1);
        }

        for (int y = 1; y < height - 1; y++)
        {
            EnqueueIfBackground(0, y);
            EnqueueIfBackground(width - 1, y);
        }

        while (head < tail)
        {
            int idx = queue[head++];
            int x = idx % width;
            int y = idx / width;
            EnqueueIfBackground(x - 1, y);
            EnqueueIfBackground(x + 1, y);
            EnqueueIfBackground(x, y - 1);
            EnqueueIfBackground(x, y + 1);
        }

        for (int i = 0; i < pixelCount; i++)
        {
            if (mask[i] == 0 && !external[i])
                mask[i] = 255;
        }
    }

    private static int CountMaskPixels(byte[] mask)
    {
        int count = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] != 0)
                count++;
        }

        return count;
    }

    private static byte[] KeepConnectedComponentContainingClick(
        byte[] binary,
        int width,
        int height,
        int clickX,
        int clickY)
    {
        if (binary.Length != width * height || width <= 0 || height <= 0)
            return binary;

        int clampedX = Math.Clamp(clickX, 0, width - 1);
        int clampedY = Math.Clamp(clickY, 0, height - 1);
        int startIndex = (clampedY * width) + clampedX;
        if (binary[startIndex] == 0)
            return binary;

        var kept = new byte[binary.Length];
        var visited = new byte[binary.Length];
        var queue = new Queue<int>();

        visited[startIndex] = 1;
        kept[startIndex] = 255;
        queue.Enqueue(startIndex);

        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            int x = index % width;
            int y = index / width;

            for (int ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
            {
                for (int nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                {
                    if (nx == x && ny == y)
                        continue;

                    int neighbor = (ny * width) + nx;
                    if (visited[neighbor] != 0 || binary[neighbor] == 0)
                        continue;

                    visited[neighbor] = 1;
                    kept[neighbor] = 255;
                    queue.Enqueue(neighbor);
                }
            }
        }

        return kept;
    }

    // ─────────────────────────────────────────────────────
    //  IDisposable
    // ─────────────────────────────────────────────────────

    public void Dispose()
    {
        lock (_sync)
        {
            _encoderSession?.Dispose();
            _decoderSession?.Dispose();
            _encoderSession = null;
            _decoderSession = null;
            _encoderPath = null;
            _decoderPath = null;
            _cachedEmbeddings = null;
            _cachedImageVersion = -1;
        }
    }
}
