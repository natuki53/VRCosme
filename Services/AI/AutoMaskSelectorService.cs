using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.IO;

namespace VRCosme.Services.AI;

public sealed class AutoMaskSelectorService : IDisposable
{
    private readonly object _sync = new();
    private InferenceSession? _session;
    private string? _modelPath;
    private string? _inputName;
    private int _inputWidth;
    private int _inputHeight;
    private const byte MaskOn = 255;
    private const byte MaskOff = 0;

    public AutoMaskResult CreateMask(Image<Rgba32> source, string modelPath)
    {
        return CreateMask(source, modelPath, null, null, useMultiPass: true);
    }

    public AutoMaskResult CreateMask(Image<Rgba32> source, string modelPath, int seedX, int seedY)
    {
        return CreateMask(source, modelPath, seedX, seedY, useMultiPass: true);
    }

    public AutoMaskResult CreateMask(
        Image<Rgba32> source,
        string modelPath,
        int seedX,
        int seedY,
        bool useMultiPass)
    {
        return CreateMask(source, modelPath, (int?)seedX, (int?)seedY, useMultiPass);
    }

    private AutoMaskResult CreateMask(
        Image<Rgba32> source,
        string modelPath,
        int? seedX,
        int? seedY,
        bool useMultiPass)
    {
        if (source.Width <= 0 || source.Height <= 0)
            throw new ArgumentException("Invalid source image size.", nameof(source));
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("Model path is empty.", nameof(modelPath));
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("ONNX model file was not found.", modelPath);

        EnsureSession(modelPath);
        var output = useMultiPass
            ? BuildHighQualityOutputMap(source)
            : RunModel(source);
        var threshold = ComputeOtsuThreshold(output);
        var binary = seedX.HasValue && seedY.HasValue
            ? BuildSeededMask(output, source.Width, source.Height, seedX.Value, seedY.Value, threshold)
            : BuildGlobalMask(output, source.Width, source.Height, threshold);

        return new AutoMaskResult(binary, source.Width, source.Height, threshold);
    }

    private float[] BuildHighQualityOutputMap(Image<Rgba32> source)
    {
        // 高精度化: 原画像 + 左右/上下/左右上下反転の4回推論を平均化する
        var map = RunModel(source);

        using var flipped = source.Clone(ctx => ctx.Flip(FlipMode.Horizontal));
        var hMap = RunModel(flipped);
        MirrorHorizontalInPlace(hMap, source.Width, source.Height);

        using var vFlipped = source.Clone(ctx => ctx.Flip(FlipMode.Vertical));
        var vMap = RunModel(vFlipped);
        MirrorVerticalInPlace(vMap, source.Width, source.Height);

        using var hvFlipped = source.Clone(ctx =>
        {
            ctx.Flip(FlipMode.Horizontal);
            ctx.Flip(FlipMode.Vertical);
        });
        var hvMap = RunModel(hvFlipped);
        MirrorHorizontalInPlace(hvMap, source.Width, source.Height);
        MirrorVerticalInPlace(hvMap, source.Width, source.Height);

        for (int i = 0; i < map.Length; i++)
            map[i] = Math.Clamp((map[i] + hMap[i] + vMap[i] + hvMap[i]) * 0.25f, 0f, 1f);

        return map;
    }

    private float[] RunModel(Image<Rgba32> source)
    {
        var tensor = BuildInputTensor(source, _inputWidth, _inputHeight);
        var input = NamedOnnxValue.CreateFromTensor(_inputName!, tensor);
        using var results = _session!.Run([input]);
        return ExtractOutputMap(results, source.Width, source.Height);
    }

    private static void MirrorHorizontalInPlace(float[] map, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int half = width / 2;
            for (int x = 0; x < half; x++)
            {
                int left = row + x;
                int right = row + (width - 1 - x);
                (map[left], map[right]) = (map[right], map[left]);
            }
        }
    }

    private static void MirrorVerticalInPlace(float[] map, int width, int height)
    {
        int half = height / 2;
        for (int y = 0; y < half; y++)
        {
            int topRow = y * width;
            int bottomRow = (height - 1 - y) * width;
            for (int x = 0; x < width; x++)
            {
                int top = topRow + x;
                int bottom = bottomRow + x;
                (map[top], map[bottom]) = (map[bottom], map[top]);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _session?.Dispose();
            _session = null;
            _modelPath = null;
            _inputName = null;
            _inputWidth = 0;
            _inputHeight = 0;
        }
    }

    private void EnsureSession(string modelPath)
    {
        lock (_sync)
        {
            if (_session != null
                && _modelPath != null
                && string.Equals(_modelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _session?.Dispose();
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            _session = new InferenceSession(modelPath, options);
            _modelPath = modelPath;

            var input = _session.InputMetadata.First();
            _inputName = input.Key;
            ResolveInputSize(input.Value.Dimensions, out _inputWidth, out _inputHeight);
        }
    }

    private static void ResolveInputSize(IReadOnlyList<int> dims, out int width, out int height)
    {
        width = 320;
        height = 320;

        if (dims.Count < 4) return;
        int h = dims[^2];
        int w = dims[^1];
        if (h > 0) height = h;
        if (w > 0) width = w;
    }

    private static DenseTensor<float> BuildInputTensor(Image<Rgba32> source, int targetWidth, int targetHeight)
    {
        using var resized = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(targetWidth, targetHeight),
            Sampler = KnownResamplers.Bicubic,
            Mode = ResizeMode.Stretch
        }));

        var tensor = new DenseTensor<float>([1, 3, targetHeight, targetWidth]);
        const float meanR = 0.485f;
        const float meanG = 0.456f;
        const float meanB = 0.406f;
        const float stdR = 0.229f;
        const float stdG = 0.224f;
        const float stdB = 0.225f;

        for (int y = 0; y < targetHeight; y++)
        {
            var row = resized.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < targetWidth; x++)
            {
                var px = row[x];
                float r = px.R / 255f;
                float g = px.G / 255f;
                float b = px.B / 255f;
                tensor[0, 0, y, x] = (r - meanR) / stdR;
                tensor[0, 1, y, x] = (g - meanG) / stdG;
                tensor[0, 2, y, x] = (b - meanB) / stdB;
            }
        }

        return tensor;
    }

    private static float[] ExtractOutputMap(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs, int outWidth, int outHeight)
    {
        foreach (var output in outputs)
        {
            if (output.AsTensor<float>() is not Tensor<float> tensor)
                continue;

            var dims = tensor.Dimensions.ToArray();
            if (dims.Length < 2)
                continue;

            int mapHeight;
            int mapWidth;
            int offset;
            int stride;

            if (dims.Length == 4)
            {
                mapHeight = dims[^2];
                mapWidth = dims[^1];
                stride = mapHeight * mapWidth;
                offset = 0;
            }
            else if (dims.Length == 3)
            {
                mapHeight = dims[^2];
                mapWidth = dims[^1];
                stride = mapHeight * mapWidth;
                offset = 0;
            }
            else
            {
                mapHeight = dims[0];
                mapWidth = dims[1];
                stride = mapHeight * mapWidth;
                offset = 0;
            }

            if (mapHeight <= 0 || mapWidth <= 0 || stride <= 0)
                continue;

            var raw = tensor.ToArray();
            if (raw.Length < offset + stride)
                continue;

            var source = new float[stride];
            Array.Copy(raw, offset, source, 0, stride);
            NormalizeInPlace(source);
            return ResizeBilinear(source, mapWidth, mapHeight, outWidth, outHeight);
        }

        throw new InvalidOperationException("No valid float output tensor found in ONNX inference result.");
    }

    private static void NormalizeInPlace(float[] values)
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        for (int i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }

        var range = max - min;
        if (range < 1e-6f)
        {
            Array.Fill(values, 0f);
            return;
        }

        for (int i = 0; i < values.Length; i++)
            values[i] = (values[i] - min) / range;
    }

    private static float[] ResizeBilinear(float[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (sourceWidth == targetWidth && sourceHeight == targetHeight)
            return source;

        var target = new float[targetWidth * targetHeight];
        double yScale = targetHeight > 1 ? (double)(sourceHeight - 1) / (targetHeight - 1) : 0.0;
        double xScale = targetWidth > 1 ? (double)(sourceWidth - 1) / (targetWidth - 1) : 0.0;

        for (int y = 0; y < targetHeight; y++)
        {
            double fy = y * yScale;
            int y0 = (int)fy;
            int y1 = Math.Min(y0 + 1, sourceHeight - 1);
            float wy = (float)(fy - y0);

            int dstRow = y * targetWidth;
            int srcRow0 = y0 * sourceWidth;
            int srcRow1 = y1 * sourceWidth;

            for (int x = 0; x < targetWidth; x++)
            {
                double fx = x * xScale;
                int x0 = (int)fx;
                int x1 = Math.Min(x0 + 1, sourceWidth - 1);
                float wx = (float)(fx - x0);

                float top = source[srcRow0 + x0] + (source[srcRow0 + x1] - source[srcRow0 + x0]) * wx;
                float bottom = source[srcRow1 + x0] + (source[srcRow1 + x1] - source[srcRow1 + x0]) * wx;
                target[dstRow + x] = top + (bottom - top) * wy;
            }
        }

        return target;
    }

    private static float ComputeOtsuThreshold(float[] values)
    {
        var hist = new int[256];
        for (int i = 0; i < values.Length; i++)
        {
            int b = (int)Math.Clamp(values[i] * 255f, 0f, 255f);
            hist[b]++;
        }

        int total = values.Length;
        float sum = 0f;
        for (int i = 0; i < hist.Length; i++)
            sum += i * hist[i];

        int wB = 0;
        float sumB = 0f;
        float maxVar = 0f;
        int threshold = 127;

        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;

            int wF = total - wB;
            if (wF == 0) break;

            sumB += t * hist[t];
            float mB = sumB / wB;
            float mF = (sum - sumB) / wF;
            float between = wB * wF * (mB - mF) * (mB - mF);
            if (between > maxVar)
            {
                maxVar = between;
                threshold = t;
            }
        }

        return Math.Clamp(threshold / 255f, 0.2f, 0.8f);
    }

    private static byte[] Threshold(float[] values, float threshold)
    {
        var mask = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
            mask[i] = values[i] >= threshold ? MaskOn : MaskOff;
        return mask;
    }

    private static byte[] BuildGlobalMask(float[] probability, int width, int height, float otsuThreshold)
    {
        var mask = Threshold(probability, otsuThreshold);
        KeepLargestConnectedComponent(mask, width, height);
        mask = MorphClose(mask, width, height);
        mask = MorphOpen(mask, width, height);
        FillHoles(mask, width, height);
        KeepLargestConnectedComponent(mask, width, height);
        return mask;
    }

    private static byte[] BuildSeededMask(float[] probability, int width, int height, int seedX, int seedY, float otsuThreshold)
    {
        seedX = Math.Clamp(seedX, 0, width - 1);
        seedY = Math.Clamp(seedY, 0, height - 1);
        int seedIdx = seedY * width + seedX;
        float seedConfidence = probability[seedIdx];

        return BuildSeedSimilarityMask(probability, width, height, seedX, seedY, seedConfidence, otsuThreshold);
    }

    private static byte[] BuildSeedSimilarityMask(
        float[] probability,
        int width,
        int height,
        int seedX,
        int seedY,
        float seedConfidence,
        float otsuThreshold)
    {
        ComputeLocalStats(probability, width, height, seedX, seedY, radius: 8, out _, out float localStd);
        var affinity = BuildSeedAffinityMap(probability, width, height, seedX, seedY, seedConfidence);

        float highThreshold = Math.Clamp(0.84f - localStd * 1.15f, 0.58f, 0.93f);
        if (seedConfidence < otsuThreshold)
            highThreshold = Math.Max(0.56f, highThreshold - 0.05f);
        float lowThreshold = Math.Clamp(highThreshold - 0.23f, 0.30f, 0.88f);

        int root = seedY * width + seedX;
        var mask = RegionGrowFromSeed(affinity, width, height, root, highThreshold, lowThreshold);
        if (CountNonZero(mask) == 0)
            return new byte[probability.Length];

        mask = MorphClose(mask, width, height);
        mask = MorphOpen(mask, width, height);
        FillHoles(mask, width, height);
        mask = ExpandMaskByConnectivity(mask, affinity, probability, width, height, seedConfidence, otsuThreshold);
        mask = MorphClose(mask, width, height);
        FillHoles(mask, width, height);

        int anchor = root;
        if (mask[anchor] == 0)
        {
            int fallback = FindNearestMaskPixel(
                mask,
                width,
                height,
                seedX,
                seedY,
                Math.Max(16, Math.Min(width, height) / 12));
            if (fallback < 0)
                return new byte[probability.Length];

            anchor = fallback;
        }

        if (!KeepComponentContainingIndex(mask, width, height, anchor))
            return new byte[probability.Length];

        return mask;
    }

    private static byte[] ExpandMaskByConnectivity(
        byte[] seedMask,
        float[] affinity,
        float[] probability,
        int width,
        int height,
        float seedConfidence,
        float otsuThreshold)
    {
        if (seedMask.Length == 0)
            return seedMask;

        var merged = (byte[])seedMask.Clone();
        var anchor = DilateN(seedMask, width, height, iterations: 12);

        float relaxedAffinityThreshold = Math.Clamp(0.46f - (otsuThreshold - 0.5f) * 0.12f, 0.34f, 0.58f);
        var affinityCandidate = Threshold(affinity, relaxedAffinityThreshold);
        MergeTouchingComponents(merged, anchor, affinityCandidate, width, height);

        float objectnessThreshold = Math.Clamp(Math.Min(otsuThreshold * 0.9f, seedConfidence - 0.08f), 0.22f, 0.70f);
        var objectnessCandidate = Threshold(probability, objectnessThreshold);
        MergeTouchingComponents(merged, anchor, objectnessCandidate, width, height);

        int imagePixels = width * height;
        int mergedCount = CountNonZero(merged);
        int seedCount = CountNonZero(seedMask);
        // 誤検出で画面全体に膨張した場合は元のシード結果に戻す
        if (mergedCount > imagePixels * 0.78 || mergedCount > Math.Max(seedCount * 9, imagePixels / 8))
            return seedMask;

        return mergedCount > seedCount ? merged : seedMask;
    }

    private static void MergeTouchingComponents(
        byte[] merged,
        byte[] anchorMask,
        byte[] candidateMask,
        int width,
        int height)
    {
        if (merged.Length != candidateMask.Length || merged.Length != anchorMask.Length)
            return;

        var visited = new bool[candidateMask.Length];
        var queue = new Queue<int>(1024);
        var component = new List<int>(1024);

        for (int i = 0; i < candidateMask.Length; i++)
        {
            if (candidateMask[i] == MaskOff || visited[i])
                continue;

            bool touchesAnchor = false;
            component.Clear();
            visited[i] = true;
            queue.Enqueue(i);

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                component.Add(idx);
                if (anchorMask[idx] != MaskOff)
                    touchesAnchor = true;

                int x = idx % width;
                int y = idx / width;
                Enqueue(x - 1, y);
                Enqueue(x + 1, y);
                Enqueue(x, y - 1);
                Enqueue(x, y + 1);
            }

            if (!touchesAnchor)
                continue;

            foreach (var idx in component)
            {
                merged[idx] = MaskOn;
                anchorMask[idx] = MaskOn;
            }
        }

        void Enqueue(int nx, int ny)
        {
            if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                return;

            int n = ny * width + nx;
            if (visited[n] || candidateMask[n] == MaskOff)
                return;

            visited[n] = true;
            queue.Enqueue(n);
        }
    }

    private static byte[] DilateN(byte[] source, int width, int height, int iterations)
    {
        iterations = Math.Max(0, iterations);
        var current = (byte[])source.Clone();
        for (int i = 0; i < iterations; i++)
            current = Dilate3x3(current, width, height);
        return current;
    }

    private static float[] BuildSeedAffinityMap(
        float[] probability,
        int width,
        int height,
        int seedX,
        int seedY,
        float seedConfidence)
    {
        var affinity = new float[probability.Length];
        double sigma = Math.Max(24.0, Math.Min(width, height) * 0.22);
        double sigmaSq2 = 2.0 * sigma * sigma;

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            long dy = y - seedY;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                long dx = x - seedX;

                float similarity = 1f - Math.Abs(probability[idx] - seedConfidence);
                similarity = Math.Clamp(similarity, 0f, 1f);

                double distanceSq = (double)(dx * dx + dy * dy);
                float spatial = (float)Math.Exp(-distanceSq / sigmaSq2);
                affinity[idx] = Math.Clamp(similarity * (0.55f + 0.45f * spatial), 0f, 1f);
            }
        }

        return affinity;
    }

    private static void ComputeLocalStats(
        float[] probability,
        int width,
        int height,
        int centerX,
        int centerY,
        int radius,
        out float mean,
        out float std)
    {
        int minX = Math.Max(0, centerX - radius);
        int maxX = Math.Min(width - 1, centerX + radius);
        int minY = Math.Max(0, centerY - radius);
        int maxY = Math.Min(height - 1, centerY + radius);

        float sum = 0f;
        float sumSq = 0f;
        int count = 0;
        for (int y = minY; y <= maxY; y++)
        {
            int row = y * width;
            for (int x = minX; x <= maxX; x++)
            {
                float v = probability[row + x];
                sum += v;
                sumSq += v * v;
                count++;
            }
        }

        if (count <= 0)
        {
            mean = 0f;
            std = 0f;
            return;
        }

        mean = sum / count;
        float variance = Math.Max(0f, (sumSq / count) - (mean * mean));
        std = (float)Math.Sqrt(variance);
    }

    private static byte[] RegionGrowFromSeed(
        float[] probability,
        int width,
        int height,
        int rootIndex,
        float highThreshold,
        float lowThreshold)
    {
        int pixelCount = width * height;
        var mask = new byte[pixelCount];
        var visited = new bool[pixelCount];
        var queue = new int[pixelCount];

        int head = 0, tail = 0;
        queue[tail++] = rootIndex;
        visited[rootIndex] = true;

        while (head < tail)
        {
            int idx = queue[head++];
            float center = probability[idx];
            if (center < lowThreshold)
                continue;

            mask[idx] = MaskOn;
            int x = idx % width;
            int y = idx / width;

            Visit(x - 1, y, center);
            Visit(x + 1, y, center);
            Visit(x, y - 1, center);
            Visit(x, y + 1, center);
            Visit(x - 1, y - 1, center);
            Visit(x + 1, y - 1, center);
            Visit(x - 1, y + 1, center);
            Visit(x + 1, y + 1, center);
        }

        return mask;

        void Visit(int nx, int ny, float centerConfidence)
        {
            if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) return;
            int n = ny * width + nx;
            if (visited[n]) return;
            visited[n] = true;

            float p = probability[n];
            if (p < lowThreshold) return;

            // 境界リーク抑制: 急激な信頼度低下かつ高信頼画素でない場合は拡張しない
            if (centerConfidence - p > 0.40f && p < highThreshold)
                return;

            queue[tail++] = n;
        }
    }

    private static int FindNearestMaskPixel(
        byte[] mask,
        int width,
        int height,
        int seedX,
        int seedY,
        int maxRadius = int.MaxValue)
    {
        int bestIndex = -1;
        long bestDistanceSq = long.MaxValue;
        int pixelCount = width * height;
        long maxDistanceSq = maxRadius == int.MaxValue
            ? long.MaxValue
            : (long)maxRadius * maxRadius;

        for (int i = 0; i < pixelCount; i++)
        {
            if (mask[i] == 0) continue;

            int x = i % width;
            int y = i / width;
            long dx = x - seedX;
            long dy = y - seedY;
            long distanceSq = dx * dx + dy * dy;
            if (distanceSq > maxDistanceSq)
                continue;
            if (distanceSq >= bestDistanceSq) continue;

            bestDistanceSq = distanceSq;
            bestIndex = i;
            if (distanceSq == 0)
                break;
        }

        return bestIndex;
    }

    private static int CountNonZero(byte[] mask)
    {
        int count = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] != 0)
                count++;
        }

        return count;
    }

    private static byte[] MorphClose(byte[] source, int width, int height)
    {
        var dilated = Dilate3x3(source, width, height);
        return Erode3x3(dilated, width, height);
    }

    private static byte[] MorphOpen(byte[] source, int width, int height)
    {
        var eroded = Erode3x3(source, width, height);
        return Dilate3x3(eroded, width, height);
    }

    private static byte[] Dilate3x3(byte[] source, int width, int height)
    {
        var result = new byte[source.Length];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                bool on = false;
                for (int oy = -1; oy <= 1 && !on; oy++)
                {
                    int ny = y + oy;
                    if ((uint)ny >= (uint)height) continue;
                    int nrow = ny * width;
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int nx = x + ox;
                        if ((uint)nx >= (uint)width) continue;
                        if (source[nrow + nx] != 0)
                        {
                            on = true;
                            break;
                        }
                    }
                }

                result[row + x] = on ? MaskOn : MaskOff;
            }
        }

        return result;
    }

    private static byte[] Erode3x3(byte[] source, int width, int height)
    {
        var result = new byte[source.Length];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                bool on = true;
                for (int oy = -1; oy <= 1 && on; oy++)
                {
                    int ny = y + oy;
                    if ((uint)ny >= (uint)height)
                    {
                        on = false;
                        break;
                    }

                    int nrow = ny * width;
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int nx = x + ox;
                        if ((uint)nx >= (uint)width || source[nrow + nx] == 0)
                        {
                            on = false;
                            break;
                        }
                    }
                }

                result[row + x] = on ? MaskOn : MaskOff;
            }
        }

        return result;
    }

    private static void FillHoles(byte[] mask, int width, int height)
    {
        int pixelCount = width * height;
        var external = new bool[pixelCount];
        var queue = new int[pixelCount];
        int head = 0, tail = 0;

        void EnqueueIfBackground(int x, int y)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
            int idx = y * width + x;
            if (external[idx] || mask[idx] != 0) return;
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
                mask[i] = MaskOn;
        }
    }

    private static void KeepLargestConnectedComponent(byte[] mask, int width, int height)
    {
        int pixelCount = width * height;
        var visited = new bool[pixelCount];
        var queue = new int[pixelCount];
        var best = new List<int>(pixelCount / 4);
        var current = new List<int>(4096);

        for (int i = 0; i < pixelCount; i++)
        {
            if (mask[i] == 0 || visited[i]) continue;
            current.Clear();

            int head = 0, tail = 0;
            queue[tail++] = i;
            visited[i] = true;

            while (head < tail)
            {
                int idx = queue[head++];
                current.Add(idx);
                int x = idx % width;
                int y = idx / width;

                TryEnqueue(x - 1, y);
                TryEnqueue(x + 1, y);
                TryEnqueue(x, y - 1);
                TryEnqueue(x, y + 1);
            }

            if (current.Count > best.Count)
            {
                best.Clear();
                best.AddRange(current);
            }

            void TryEnqueue(int nx, int ny)
            {
                if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) return;
                int n = ny * width + nx;
                if (visited[n] || mask[n] == 0) return;
                visited[n] = true;
                queue[tail++] = n;
            }
        }

        if (best.Count == 0)
        {
            Array.Clear(mask);
            return;
        }

        Array.Clear(mask);
        foreach (var idx in best)
            mask[idx] = 255;
    }

    private static bool KeepComponentContainingIndex(byte[] mask, int width, int height, int startIndex)
    {
        int pixelCount = width * height;
        if (pixelCount <= 0)
            return false;

        var queue = new int[pixelCount];
        if ((uint)startIndex >= (uint)pixelCount || mask[startIndex] == 0)
            return false;

        var keep = new bool[pixelCount];
        int head = 0, tail = 0;
        queue[tail++] = startIndex;
        keep[startIndex] = true;

        while (head < tail)
        {
            int idx = queue[head++];
            int x = idx % width;
            int y = idx / width;

            TryEnqueue(x - 1, y);
            TryEnqueue(x + 1, y);
            TryEnqueue(x, y - 1);
            TryEnqueue(x, y + 1);
        }

        for (int i = 0; i < pixelCount; i++)
            mask[i] = keep[i] ? MaskOn : MaskOff;

        return true;

        void TryEnqueue(int nx, int ny)
        {
            if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) return;
            int n = ny * width + nx;
            if (keep[n] || mask[n] == 0) return;
            keep[n] = true;
            queue[tail++] = n;
        }
    }
}

public readonly record struct AutoMaskResult(
    byte[] Mask,
    int Width,
    int Height,
    float Threshold
);
