using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using VRCosme.Models;

namespace VRCosme.Services.ImageProcessing;

/// <summary>マスク処理アルゴリズム群</summary>
internal static class MaskProcessingService
{
    private const byte MaskOnValue = 255;
    private const byte MaskOffValue = 0;

    // ───────── 色ベース選択 ─────────

    internal static byte[] BuildColorSelectionMask(
        Image<Rgba32> source,
        int seedX,
        int seedY,
        int colorError,
        int connectivity,
        int gapClosing,
        bool antialias)
    {
        int width = source.Width;
        int height = source.Height;
        int pixelCount = width * height;
        var mask = new byte[pixelCount];
        if (pixelCount == 0)
            return mask;

        seedX = Math.Clamp(seedX, 0, width - 1);
        seedY = Math.Clamp(seedY, 0, height - 1);
        colorError = Math.Clamp(colorError, 0, 80);
        connectivity = connectivity == 8 ? 8 : 4;
        gapClosing = Math.Clamp(gapClosing, 0, 6);

        var red = new byte[pixelCount];
        var green = new byte[pixelCount];
        var blue = new byte[pixelCount];
        for (int y = 0; y < height; y++)
        {
            var row = source.DangerousGetPixelRowMemory(y).Span;
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                red[idx] = row[x].R;
                green[idx] = row[x].G;
                blue[idx] = row[x].B;
            }
        }

        int seedIndex = seedY * width + seedX;
        var (seedR, seedG, seedB) = ComputeSeedMedianColor(red, green, blue, width, height, seedX, seedY);
        int neighborTolerance = Math.Clamp((int)Math.Round(colorError * 0.55), 2, 64);

        var visited = new bool[pixelCount];
        var queue = new int[pixelCount];
        int head = 0;
        int tail = 0;

        visited[seedIndex] = true;
        queue[tail++] = seedIndex;

        while (head < tail)
        {
            int idx = queue[head++];
            mask[idx] = MaskOnValue;

            int x = idx % width;
            int y = idx / width;

            EnqueueIfMatch(x - 1, y, idx);
            EnqueueIfMatch(x + 1, y, idx);
            EnqueueIfMatch(x, y - 1, idx);
            EnqueueIfMatch(x, y + 1, idx);

            if (connectivity == 8)
            {
                EnqueueIfMatch(x - 1, y - 1, idx);
                EnqueueIfMatch(x + 1, y - 1, idx);
                EnqueueIfMatch(x - 1, y + 1, idx);
                EnqueueIfMatch(x + 1, y + 1, idx);
            }
        }

        if (gapClosing > 0)
            ApplyGapClosing(mask, width, height, connectivity, gapClosing);

        SuppressMaskNoise(mask, width, height, connectivity, colorError);

        if (antialias)
            ApplyMaskAntialias(mask, width, height, connectivity);

        mask[seedIndex] = MaskOnValue;
        return mask;

        void EnqueueIfMatch(int nx, int ny, int fromIndex)
        {
            if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                return;

            int n = ny * width + nx;
            if (visited[n])
                return;

            visited[n] = true;
            if (!IsColorWithinTolerance(red[n], green[n], blue[n], seedR, seedG, seedB, colorError))
                return;
            if (!IsColorWithinTolerance(red[n], green[n], blue[n], red[fromIndex], green[fromIndex], blue[fromIndex], neighborTolerance))
                return;

            queue[tail++] = n;
        }
    }

    internal static bool IsColorWithinTolerance(
        byte r, byte g, byte b,
        byte seedR, byte seedG, byte seedB,
        int tolerance)
    {
        return Math.Abs(r - seedR) <= tolerance
            && Math.Abs(g - seedG) <= tolerance
            && Math.Abs(b - seedB) <= tolerance;
    }

    internal static (byte R, byte G, byte B) ComputeSeedMedianColor(
        byte[] red, byte[] green, byte[] blue,
        int width, int height,
        int seedX, int seedY)
    {
        var rs = new int[9];
        var gs = new int[9];
        var bs = new int[9];
        int count = 0;

        for (int oy = -1; oy <= 1; oy++)
        {
            int y = seedY + oy;
            if ((uint)y >= (uint)height)
                continue;

            int rowOffset = y * width;
            for (int ox = -1; ox <= 1; ox++)
            {
                int x = seedX + ox;
                if ((uint)x >= (uint)width)
                    continue;

                int idx = rowOffset + x;
                rs[count] = red[idx];
                gs[count] = green[idx];
                bs[count] = blue[idx];
                count++;
            }
        }

        if (count <= 0)
        {
            int index = seedY * width + seedX;
            return (red[index], green[index], blue[index]);
        }

        Array.Sort(rs, 0, count);
        Array.Sort(gs, 0, count);
        Array.Sort(bs, 0, count);
        int mid = count / 2;
        return ((byte)rs[mid], (byte)gs[mid], (byte)bs[mid]);
    }

    // ───────── モルフォロジー / ノイズ除去 ─────────

    internal static void SuppressMaskNoise(byte[] mask, int width, int height, int connectivity, int colorError)
    {
        if (mask.Length == 0 || width < 3 || height < 3)
            return;

        connectivity = connectivity == 8 ? 8 : 4;
        int passes = colorError >= 24 ? 2 : 1;
        int fillThreshold = connectivity == 8 ? 7 : 4;
        var snapshot = new byte[mask.Length];

        for (int pass = 0; pass < passes; pass++)
        {
            Buffer.BlockCopy(mask, 0, snapshot, 0, mask.Length);

            for (int y = 1; y < height - 1; y++)
            {
                int rowOffset = y * width;
                for (int x = 1; x < width - 1; x++)
                {
                    int idx = rowOffset + x;
                    int neighborCount = CountSelectedNeighbors(snapshot, width, idx, connectivity);

                    if (snapshot[idx] == MaskOnValue)
                    {
                        if (neighborCount <= 1)
                            mask[idx] = MaskOffValue;
                    }
                    else
                    {
                        if (neighborCount >= fillThreshold)
                            mask[idx] = MaskOnValue;
                    }
                }
            }
        }
    }

    internal static void ApplyGapClosing(byte[] mask, int width, int height, int connectivity, int steps)
    {
        if (steps <= 0 || mask.Length == 0 || width <= 1 || height <= 1)
            return;

        connectivity = connectivity == 8 ? 8 : 4;
        steps = Math.Clamp(steps, 0, 6);
        var temp = new byte[mask.Length];

        for (int i = 0; i < steps; i++)
        {
            DilateMask(mask, temp, width, height, connectivity);
            ErodeMask(temp, mask, width, height, connectivity);
        }
    }

    internal static void DilateMask(byte[] source, byte[] destination, int width, int height, int connectivity)
    {
        Array.Clear(destination, 0, destination.Length);

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                if (source[idx] == 0)
                    continue;

                destination[idx] = MaskOnValue;
                if (x > 0) destination[idx - 1] = MaskOnValue;
                if (x + 1 < width) destination[idx + 1] = MaskOnValue;
                if (y > 0) destination[idx - width] = MaskOnValue;
                if (y + 1 < height) destination[idx + width] = MaskOnValue;

                if (connectivity == 8)
                {
                    if (x > 0 && y > 0) destination[idx - width - 1] = MaskOnValue;
                    if (x + 1 < width && y > 0) destination[idx - width + 1] = MaskOnValue;
                    if (x > 0 && y + 1 < height) destination[idx + width - 1] = MaskOnValue;
                    if (x + 1 < width && y + 1 < height) destination[idx + width + 1] = MaskOnValue;
                }
            }
        }
    }

    internal static void ErodeMask(byte[] source, byte[] destination, int width, int height, int connectivity)
    {
        Array.Clear(destination, 0, destination.Length);

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                if (source[idx] == 0)
                    continue;

                bool keep = source[idx] > 0
                    && IsOn(source, width, height, x - 1, y)
                    && IsOn(source, width, height, x + 1, y)
                    && IsOn(source, width, height, x, y - 1)
                    && IsOn(source, width, height, x, y + 1);

                if (keep && connectivity == 8)
                {
                    keep = IsOn(source, width, height, x - 1, y - 1)
                        && IsOn(source, width, height, x + 1, y - 1)
                        && IsOn(source, width, height, x - 1, y + 1)
                        && IsOn(source, width, height, x + 1, y + 1);
                }

                if (keep)
                    destination[idx] = MaskOnValue;
            }
        }
    }

    internal static bool IsOn(byte[] data, int width, int height, int x, int y)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            return false;

        return data[y * width + x] > 0;
    }

    // ───────── アンチエイリアス ─────────

    internal static void ApplyMaskAntialias(byte[] mask, int width, int height, int connectivity)
    {
        if (mask.Length == 0 || width < 3 || height < 3)
            return;

        connectivity = connectivity == 8 ? 8 : 4;
        var snapshot = (byte[])mask.Clone();
        int maxNeighbors = connectivity == 8 ? 8 : 4;

        for (int y = 1; y < height - 1; y++)
        {
            int rowOffset = y * width;
            for (int x = 1; x < width - 1; x++)
            {
                int idx = rowOffset + x;
                if (snapshot[idx] == 0)
                    continue;

                int neighborCount = CountSelectedNeighbors(snapshot, width, idx, connectivity);
                if (neighborCount >= maxNeighbors)
                    continue;

                int softened = connectivity == 8
                    ? 120 + (neighborCount * 16)
                    : 136 + (neighborCount * 24);
                mask[idx] = (byte)Math.Clamp(softened, 96, 255);
            }
        }
    }

    internal static int CountSelectedNeighbors(byte[] mask, int width, int idx, int connectivity)
    {
        int count = 0;

        if (mask[idx - width] == MaskOnValue) count++;
        if (mask[idx + width] == MaskOnValue) count++;
        if (mask[idx - 1] == MaskOnValue) count++;
        if (mask[idx + 1] == MaskOnValue) count++;

        if (connectivity == 8)
        {
            if (mask[idx - width - 1] == MaskOnValue) count++;
            if (mask[idx - width + 1] == MaskOnValue) count++;
            if (mask[idx + width - 1] == MaskOnValue) count++;
            if (mask[idx + width + 1] == MaskOnValue) count++;
        }

        return count;
    }

    // ───────── リサイズ ─────────

    internal static byte[] ResizeMaskNearest(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (sourceWidth == targetWidth && sourceHeight == targetHeight)
            return (byte[])source.Clone();

        var resized = new byte[targetWidth * targetHeight];
        for (int y = 0; y < targetHeight; y++)
        {
            int srcY = (int)((long)y * sourceHeight / targetHeight);
            int srcRow = srcY * sourceWidth;
            int dstRow = y * targetWidth;
            for (int x = 0; x < targetWidth; x++)
            {
                int srcX = (int)((long)x * sourceWidth / targetWidth);
                resized[dstRow + x] = source[srcRow + srcX];
            }
        }

        return resized;
    }

    internal static byte[] ResizeMaskBilinear(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (sourceWidth == targetWidth && sourceHeight == targetHeight)
            return (byte[])source.Clone();

        var resized = new byte[targetWidth * targetHeight];

        for (int y = 0; y < targetHeight; y++)
        {
            double srcY = ((y + 0.5) * sourceHeight / targetHeight) - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(srcY), 0, sourceHeight - 1);
            int y1 = Math.Clamp(y0 + 1, 0, sourceHeight - 1);
            float fy = (float)(srcY - y0);
            if (fy < 0f) fy = 0f;
            if (fy > 1f) fy = 1f;

            int row0 = y0 * sourceWidth;
            int row1 = y1 * sourceWidth;
            int dstRow = y * targetWidth;

            for (int x = 0; x < targetWidth; x++)
            {
                double srcX = ((x + 0.5) * sourceWidth / targetWidth) - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(srcX), 0, sourceWidth - 1);
                int x1 = Math.Clamp(x0 + 1, 0, sourceWidth - 1);
                float fx = (float)(srcX - x0);
                if (fx < 0f) fx = 0f;
                if (fx > 1f) fx = 1f;

                float v00 = source[row0 + x0];
                float v10 = source[row0 + x1];
                float v01 = source[row1 + x0];
                float v11 = source[row1 + x1];

                float top = v00 + (v10 - v00) * fx;
                float bottom = v01 + (v11 - v01) * fx;
                float value = top + (bottom - top) * fy;
                resized[dstRow + x] = (byte)Math.Clamp(MathF.Round(value), 0f, 255f);
            }
        }

        return resized;
    }

    internal static byte[] BuildDilatedBinaryMask(byte[] source, int width, int height, int iterations)
    {
        if (iterations <= 0 || source.Length == 0)
            return (byte[])source.Clone();

        var current = new byte[source.Length];
        for (int i = 0; i < source.Length; i++)
            current[i] = source[i] > 0 ? MaskOnValue : MaskOffValue;

        var next = new byte[source.Length];
        for (int iter = 0; iter < iterations; iter++)
        {
            Array.Clear(next);

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = row + x;
                    if (current[idx] == MaskOffValue)
                        continue;

                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int ny = y + oy;
                        if ((uint)ny >= (uint)height) continue;
                        int nrow = ny * width;
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int nx = x + ox;
                            if ((uint)nx >= (uint)width) continue;
                            next[nrow + nx] = MaskOnValue;
                        }
                    }
                }
            }

            (current, next) = (next, current);
        }

        return current;
    }

    // ───────── ポリゴン塗りつぶし ─────────

    internal static bool FillPolygon(
        MaskLayer layer,
        IReadOnlyList<(double X, double Y)> points,
        bool eraseMode)
    {
        var vertices = NormalizePolygonPoints(points, layer.Width, layer.Height);
        if (vertices.Count < 3)
            return false;

        double minYValue = vertices[0].Y;
        double maxYValue = vertices[0].Y;
        for (int i = 1; i < vertices.Count; i++)
        {
            var y = vertices[i].Y;
            if (y < minYValue) minYValue = y;
            if (y > maxYValue) maxYValue = y;
        }

        int minY = Math.Max(0, (int)Math.Floor(minYValue - 1.0));
        int maxY = Math.Min(layer.Height - 1, (int)Math.Ceiling(maxYValue + 1.0));
        if (minY > maxY)
            return false;

        var changed = false;
        var intersections = new List<double>(vertices.Count);
        byte fillValue = eraseMode ? MaskOffValue : MaskOnValue;

        for (int py = minY; py <= maxY; py++)
        {
            intersections.Clear();
            double scanY = py + 0.5;

            for (int i = 0; i < vertices.Count; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % vertices.Count];
                bool crosses = (a.Y <= scanY && b.Y > scanY) || (b.Y <= scanY && a.Y > scanY);
                if (!crosses) continue;

                double x = a.X + (scanY - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                intersections.Add(x);
            }

            if (intersections.Count < 2)
                continue;

            intersections.Sort();
            for (int i = 0; i + 1 < intersections.Count; i += 2)
            {
                double left = intersections[i];
                double right = intersections[i + 1];
                if (right < left)
                    (left, right) = (right, left);

                int minX = Math.Max(0, (int)Math.Ceiling(left - 0.5));
                int maxX = Math.Min(layer.Width - 1, (int)Math.Floor(right - 0.5));
                if (minX > maxX) continue;

                int row = py * layer.Width;
                for (int px = minX; px <= maxX; px++)
                {
                    int idx = row + px;
                    if (layer.MaskData[idx] == fillValue) continue;
                    layer.SetMaskPixel(idx, fillValue);
                    changed = true;
                }
            }
        }

        return changed;
    }

    internal static List<(double X, double Y)> NormalizePolygonPoints(
        IReadOnlyList<(double X, double Y)> points,
        int width, int height)
    {
        var normalized = new List<(double X, double Y)>(points.Count);
        if (width <= 0 || height <= 0)
            return normalized;

        double maxX = width - 1;
        double maxY = height - 1;
        const double minDistanceSq = 0.25;

        for (int i = 0; i < points.Count; i++)
        {
            var point = (
                X: Math.Clamp(points[i].X, 0, maxX),
                Y: Math.Clamp(points[i].Y, 0, maxY));

            if (normalized.Count == 0)
            {
                normalized.Add(point);
                continue;
            }

            var last = normalized[^1];
            double dx = point.X - last.X;
            double dy = point.Y - last.Y;
            if ((dx * dx + dy * dy) >= minDistanceSq)
                normalized.Add(point);
        }

        if (normalized.Count >= 3)
        {
            var first = normalized[0];
            var last = normalized[^1];
            double dx = first.X - last.X;
            double dy = first.Y - last.Y;
            if ((dx * dx + dy * dy) < minDistanceSq)
                normalized.RemoveAt(normalized.Count - 1);
        }

        return normalized;
    }
}
