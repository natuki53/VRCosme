using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using VRCosme.Models;
using VRCosme.Services.ImageProcessing;

namespace VRCosme.ViewModels;

public partial class MainViewModel
{
    private readonly object _maskSync = new();
    private const byte MaskOnValue = 255;
    private const byte MaskOffValue = 0;
    private int _maskSelectionHistorySuppressCount;
    private bool _hasSelectedMaskAdjustmentPreview;
    private MaskAdjustmentValues _selectedMaskAdjustmentPreview;

    public ObservableCollection<MaskLayer> MaskLayers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMaskLayer))]
    private MaskLayer? _selectedMaskLayer;

    [ObservableProperty] private bool _isMaskEnabled = true;

    [ObservableProperty] private bool _isMaskEditing;
    [ObservableProperty] private bool _isMaskAutoSelectMode;
    [ObservableProperty] private bool _isMaskEraseMode;
    [ObservableProperty] private double _maskCoverage;

    public bool HasMask => MaskLayers.Any(layer => layer.HasMask);
    public bool HasSelectedMaskLayer => SelectedMaskLayer != null;

    partial void OnSelectedMaskLayerChanging(MaskLayer? oldValue, MaskLayer? newValue)
    {
        if (_maskSelectionHistorySuppressCount > 0 || ReferenceEquals(oldValue, newValue))
            return;

        PushUndoSnapshot();
    }

    partial void OnSelectedMaskLayerChanged(MaskLayer? value)
    {
        lock (_maskSync)
        {
            _hasSelectedMaskAdjustmentPreview = false;
        }

        MaskCoverage = value?.CoveragePercent ?? 0;
        OnPropertyChanged(nameof(HasSelectedMaskLayer));
        SchedulePreviewUpdate();
    }

    private void SetSelectedMaskLayerWithoutHistory(MaskLayer? layer)
    {
        _maskSelectionHistorySuppressCount++;
        try
        {
            SelectedMaskLayer = layer;
        }
        finally
        {
            _maskSelectionHistorySuppressCount--;
        }
    }

    partial void OnIsMaskEditingChanged(bool value)
    {
        if (!value) return;

        if (IsMaskAutoSelectMode)
            IsMaskAutoSelectMode = false;

        if (!HasSelectedMaskLayer && HasImage)
            AddMaskLayerInternal(pushUndoSnapshot: false);
    }

    partial void OnIsMaskAutoSelectModeChanged(bool value)
    {
        if (!value) return;

        if (IsMaskEditing)
            IsMaskEditing = false;

        if (!HasSelectedMaskLayer && HasImage)
            AddMaskLayerInternal(pushUndoSnapshot: false);
    }

    [RelayCommand]
    private void AddMaskLayer()
    {
        if (!HasImage) return;
        AddMaskLayerInternal(pushUndoSnapshot: true);
    }

    [RelayCommand]
    private void RemoveSelectedMaskLayer()
    {
        if (!HasImage || SelectedMaskLayer is null) return;
        PushUndoSnapshot();
        bool becameEmpty = false;

        lock (_maskSync)
        {
            int idx = MaskLayers.IndexOf(SelectedMaskLayer);
            if (idx < 0) return;
            MaskLayers.RemoveAt(idx);
            if (MaskLayers.Count == 0)
            {
                SetSelectedMaskLayerWithoutHistory(null);
                becameEmpty = true;
            }
            else
            {
                SetSelectedMaskLayerWithoutHistory(MaskLayers[Math.Clamp(idx, 0, MaskLayers.Count - 1)]);
            }
        }

        if (becameEmpty)
        {
            IsMaskEditing = false;
            IsMaskAutoSelectMode = false;
        }

        NotifyMaskStateChanged(schedulePreview: true);
    }

    [RelayCommand]
    private void ClearMask()
    {
        if (!HasImage || SelectedMaskLayer is null || !SelectedMaskLayer.HasMask) return;

        PushUndoSnapshot();
        lock (_maskSync)
        {
            SelectedMaskLayer.ClearMask();
        }

        NotifyMaskStateChanged(schedulePreview: true);
    }

    [RelayCommand]
    private void InvertSelectedMask()
    {
        if (!HasImage || SelectedMaskLayer is null || ImageWidth <= 0 || ImageHeight <= 0)
            return;

        PushUndoSnapshot();
        lock (_maskSync)
        {
            if (SelectedMaskLayer.Width != ImageWidth || SelectedMaskLayer.Height != ImageHeight)
                SelectedMaskLayer.ResetMaskSize(ImageWidth, ImageHeight);

            var current = SelectedMaskLayer.MaskData;
            var inverted = new byte[current.Length];
            int nonZero = 0;

            for (int i = 0; i < current.Length; i++)
            {
                byte next = current[i] > 0 ? MaskOffValue : MaskOnValue;
                inverted[i] = next;
                if (next > 0)
                    nonZero++;
            }

            SelectedMaskLayer.SetMaskData(inverted, ImageWidth, ImageHeight, nonZero);
        }

        NotifyMaskStateChanged(schedulePreview: true);
    }

    public bool ApplyMaskLasso(
        IReadOnlyList<(double X, double Y)> polygonPoints,
        bool eraseMode)
    {
        if (!HasImage
            || SelectedMaskLayer is null
            || ImageWidth <= 0
            || ImageHeight <= 0
            || polygonPoints.Count < 3)
            return false;

        var snapshotBefore = CreateSnapshot();

        bool changed;
        lock (_maskSync)
        {
            if (SelectedMaskLayer.Width != ImageWidth || SelectedMaskLayer.Height != ImageHeight)
                SelectedMaskLayer.ResetMaskSize(ImageWidth, ImageHeight);

            changed = FillPolygonMaskLocked(SelectedMaskLayer, polygonPoints, eraseMode);
        }

        if (!changed)
            return false;

        PushUndoState(snapshotBefore);
        NotifyMaskStateChanged(schedulePreview: true);
        return true;
    }

    public bool ApplySelectedMaskFromBinary(byte[] maskData, int width, int height)
    {
        if (!HasImage
            || SelectedMaskLayer is null
            || ImageWidth <= 0
            || ImageHeight <= 0
            || width <= 0
            || height <= 0
            || maskData.Length != width * height)
        {
            return false;
        }

        var resized = width == ImageWidth && height == ImageHeight
            ? (byte[])maskData.Clone()
            : ResizeMaskNearest(maskData, width, height, ImageWidth, ImageHeight);

        bool changed = false;
        int nonZero = 0;

        lock (_maskSync)
        {
            if (SelectedMaskLayer.Width != ImageWidth || SelectedMaskLayer.Height != ImageHeight)
                SelectedMaskLayer.ResetMaskSize(ImageWidth, ImageHeight);

            var current = SelectedMaskLayer.MaskData;
            for (int i = 0; i < resized.Length; i++)
            {
                byte next = resized[i] > 0 ? MaskOnValue : MaskOffValue;
                resized[i] = next;
                if (next > 0) nonZero++;
                if (!changed && current[i] != next)
                    changed = true;
            }
        }

        if (!changed)
            return false;

        PushUndoSnapshot();
        lock (_maskSync)
        {
            SelectedMaskLayer.SetMaskData(resized, ImageWidth, ImageHeight, nonZero);
        }

        NotifyMaskStateChanged(schedulePreview: true);
        return true;
    }

    public bool MergeSelectedMaskFromBinary(byte[] maskData, int width, int height)
    {
        if (!HasImage
            || SelectedMaskLayer is null
            || ImageWidth <= 0
            || ImageHeight <= 0
            || width <= 0
            || height <= 0
            || maskData.Length != width * height)
        {
            return false;
        }

        bool hasExistingMask;
        lock (_maskSync)
        {
            hasExistingMask = SelectedMaskLayer.NonZeroCount > 0;
        }

        if (!hasExistingMask)
            return ApplySelectedMaskFromBinary(maskData, width, height);

        var resized = width == ImageWidth && height == ImageHeight
            ? (byte[])maskData.Clone()
            : ResizeMaskNearest(maskData, width, height, ImageWidth, ImageHeight);

        for (int i = 0; i < resized.Length; i++)
            resized[i] = resized[i] > 0 ? MaskOnValue : MaskOffValue;

        byte[] merged;
        int mergedNonZero;
        bool changed;

        lock (_maskSync)
        {
            if (SelectedMaskLayer.Width != ImageWidth || SelectedMaskLayer.Height != ImageHeight)
                SelectedMaskLayer.ResetMaskSize(ImageWidth, ImageHeight);

            var current = SelectedMaskLayer.MaskData;
            merged = (byte[])current.Clone();
            var dilatedCurrent = BuildDilatedBinaryMask(current, ImageWidth, ImageHeight, iterations: 10);
            changed = false;

            var visited = new bool[resized.Length];
            var component = new List<int>(1024);
            var queue = new Queue<int>(1024);

            for (int i = 0; i < resized.Length; i++)
            {
                if (resized[i] == MaskOffValue || visited[i])
                    continue;

                component.Clear();
                bool touchesCurrentRegion = false;
                queue.Enqueue(i);
                visited[i] = true;

                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    component.Add(idx);
                    if (dilatedCurrent[idx] != MaskOffValue)
                        touchesCurrentRegion = true;

                    int x = idx % ImageWidth;
                    int y = idx / ImageWidth;
                    Enqueue(x - 1, y);
                    Enqueue(x + 1, y);
                    Enqueue(x, y - 1);
                    Enqueue(x, y + 1);
                }

                if (!touchesCurrentRegion)
                    continue;

                foreach (var idx in component)
                {
                    if (merged[idx] == MaskOnValue)
                        continue;

                    merged[idx] = MaskOnValue;
                    changed = true;
                }
            }

            mergedNonZero = 0;
            for (int i = 0; i < merged.Length; i++)
            {
                if (merged[i] == MaskOnValue)
                    mergedNonZero++;
            }

            void Enqueue(int nx, int ny)
            {
                if ((uint)nx >= (uint)ImageWidth || (uint)ny >= (uint)ImageHeight)
                    return;

                int n = ny * ImageWidth + nx;
                if (visited[n] || resized[n] == MaskOffValue)
                    return;

                visited[n] = true;
                queue.Enqueue(n);
            }
        }

        if (!changed)
            return false;

        PushUndoSnapshot();
        lock (_maskSync)
        {
            SelectedMaskLayer.SetMaskData(merged, ImageWidth, ImageHeight, mergedNonZero);
        }

        NotifyMaskStateChanged(schedulePreview: true);
        return true;
    }

    public MaskLayerState? GetSelectedMaskLayerState()
    {
        lock (_maskSync)
        {
            return SelectedMaskLayer?.CreateState();
        }
    }

    public string GetSelectedMaskLayerName() =>
        SelectedMaskLayer?.Name ?? string.Empty;

    public void RenameSelectedMaskLayer(string? newName)
    {
        if (SelectedMaskLayer is null) return;
        var normalized = NormalizeMaskLayerName(newName);
        if (string.Equals(SelectedMaskLayer.Name, normalized, StringComparison.Ordinal))
            return;

        PushUndoSnapshot();
        SelectedMaskLayer.Name = normalized;
    }

    public MaskAdjustmentValues GetSelectedMaskAdjustmentValues()
    {
        if (SelectedMaskLayer is null)
            return new MaskAdjustmentValues(0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        return new MaskAdjustmentValues(
            SelectedMaskLayer.Brightness,
            SelectedMaskLayer.Contrast,
            SelectedMaskLayer.Gamma,
            SelectedMaskLayer.Exposure,
            SelectedMaskLayer.Saturation,
            SelectedMaskLayer.Temperature,
            SelectedMaskLayer.Tint,
            SelectedMaskLayer.Shadows,
            SelectedMaskLayer.Highlights,
            SelectedMaskLayer.Clarity,
            SelectedMaskLayer.Blur,
            SelectedMaskLayer.Sharpen,
            SelectedMaskLayer.Vignette
        );
    }

    public void ApplySelectedMaskAdjustments(MaskAdjustmentValues values)
    {
        if (SelectedMaskLayer is null) return;

        ClearSelectedMaskAdjustmentsPreview(schedulePreview: false);
        PushUndoSnapshot();
        SelectedMaskLayer.Brightness = values.Brightness;
        SelectedMaskLayer.Contrast = values.Contrast;
        SelectedMaskLayer.Gamma = values.Gamma;
        SelectedMaskLayer.Exposure = values.Exposure;
        SelectedMaskLayer.Saturation = values.Saturation;
        SelectedMaskLayer.Temperature = values.Temperature;
        SelectedMaskLayer.Tint = values.Tint;
        SelectedMaskLayer.Shadows = values.Shadows;
        SelectedMaskLayer.Highlights = values.Highlights;
        SelectedMaskLayer.Clarity = values.Clarity;
        SelectedMaskLayer.Blur = values.Blur;
        SelectedMaskLayer.Sharpen = values.Sharpen;
        SelectedMaskLayer.Vignette = values.Vignette;

        SchedulePreviewUpdate();
    }

    public void PreviewSelectedMaskAdjustments(MaskAdjustmentValues values)
    {
        if (SelectedMaskLayer is null) return;

        lock (_maskSync)
        {
            _selectedMaskAdjustmentPreview = values;
            _hasSelectedMaskAdjustmentPreview = true;
        }

        SchedulePreviewUpdate();
    }

    public void ClearSelectedMaskAdjustmentsPreview(bool schedulePreview = true)
    {
        bool hadPreview;
        lock (_maskSync)
        {
            hadPreview = _hasSelectedMaskAdjustmentPreview;
            _hasSelectedMaskAdjustmentPreview = false;
        }

        if (hadPreview && schedulePreview)
            SchedulePreviewUpdate();
    }

    private MaskLayer AddMaskLayerInternal(bool pushUndoSnapshot)
    {
        if (pushUndoSnapshot)
            PushUndoSnapshot();

        MaskLayer layer;
        lock (_maskSync)
        {
            layer = new MaskLayer(CreateDefaultMaskLayerNameLocked(), ImageWidth, ImageHeight);
            MaskLayers.Add(layer);
            SetSelectedMaskLayerWithoutHistory(layer);
        }

        NotifyMaskStateChanged(schedulePreview: true);
        return layer;
    }

    private string CreateDefaultMaskLayerNameLocked()
    {
        var used = new HashSet<int>();
        foreach (var layer in MaskLayers)
        {
            if (TryParseMaskSerial(layer.Name, out int serial))
                used.Add(serial);
        }

        int candidate = 1;
        while (used.Contains(candidate))
            candidate++;

        return $"Mask {candidate}";
    }

    private static bool TryParseMaskSerial(string? name, out int serial)
    {
        serial = 0;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var trimmed = name.Trim();
        if (!trimmed.StartsWith("Mask ", StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = trimmed.AsSpan(5).Trim();
        return int.TryParse(suffix, out serial) && serial > 0;
    }

    private static string NormalizeMaskLayerName(string? name)
    {
        var value = name?.Trim();
        return string.IsNullOrEmpty(value) ? "Mask" : value;
    }

    private static bool FillPolygonMaskLocked(
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

    private static List<(double X, double Y)> NormalizePolygonPoints(
        IReadOnlyList<(double X, double Y)> points,
        int width,
        int height)
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

    private (List<MaskLayerState> Layers, int SelectedIndex) CloneMaskSnapshot()
    {
        lock (_maskSync)
        {
            var layers = MaskLayers.Select(layer => layer.CreateState()).ToList();
            int selectedIndex = SelectedMaskLayer is null ? -1 : MaskLayers.IndexOf(SelectedMaskLayer);
            return (layers, selectedIndex);
        }
    }

    private void RestoreMaskSnapshot(IReadOnlyList<MaskLayerState> layers, int selectedIndex)
    {
        lock (_maskSync)
        {
            _hasSelectedMaskAdjustmentPreview = false;
            MaskLayers.Clear();
            foreach (var state in layers)
                MaskLayers.Add(MaskLayer.FromState(state));

            if (MaskLayers.Count == 0 || selectedIndex < 0 || selectedIndex >= MaskLayers.Count)
                SetSelectedMaskLayerWithoutHistory(MaskLayers.FirstOrDefault());
            else
                SetSelectedMaskLayerWithoutHistory(MaskLayers[selectedIndex]);
        }

        NotifyMaskStateChanged(schedulePreview: false);
    }

    private void ResetMaskForNewImage()
    {
        lock (_maskSync)
        {
            _hasSelectedMaskAdjustmentPreview = false;
            MaskLayers.Clear();
            SetSelectedMaskLayerWithoutHistory(null);
        }

        IsMaskEditing = false;
        IsMaskAutoSelectMode = false;
        MaskCoverage = 0;
        OnPropertyChanged(nameof(HasMask));
        OnPropertyChanged(nameof(HasSelectedMaskLayer));
    }

    private void EnsureMaskSizeMatchesImageOrClear()
    {
        bool changed = false;
        lock (_maskSync)
        {
            foreach (var layer in MaskLayers)
            {
                if (layer.Width == ImageWidth && layer.Height == ImageHeight)
                    continue;

                layer.ResetMaskSize(ImageWidth, ImageHeight);
                changed = true;
            }
        }

        if (changed)
            NotifyMaskStateChanged(schedulePreview: false);
    }

    private void NotifyMaskStateChanged(bool schedulePreview)
    {
        MaskCoverage = SelectedMaskLayer?.CoveragePercent ?? 0;
        OnPropertyChanged(nameof(HasMask));
        OnPropertyChanged(nameof(HasSelectedMaskLayer));

        if (schedulePreview)
            SchedulePreviewUpdate();
    }

    private List<(byte[] Mask, AdjustmentParams Params)> BuildMaskAdjustmentSequence(
        int targetWidth,
        int targetHeight,
        bool requireMaskEnabled)
    {
        var result = new List<(byte[] Mask, AdjustmentParams Params)>();
        if (requireMaskEnabled && !IsMaskEnabled)
            return result;
        if (targetWidth <= 0 || targetHeight <= 0)
            return result;

        List<MaskLayerState> states;
        int selectedIndex;
        bool hasPreview;
        MaskAdjustmentValues previewValues;
        lock (_maskSync)
        {
            states = MaskLayers.Select(layer => layer.CreateState()).ToList();
            selectedIndex = SelectedMaskLayer is null ? -1 : MaskLayers.IndexOf(SelectedMaskLayer);
            hasPreview = _hasSelectedMaskAdjustmentPreview;
            previewValues = _selectedMaskAdjustmentPreview;
        }

        for (int i = 0; i < states.Count; i++)
        {
            var state = states[i];
            if (state.NonZeroCount <= 0)
                continue;

            var p = hasPreview && i == selectedIndex
                ? BuildLayerParams(previewValues)
                : BuildLayerParams(state);
            if (p.IsDefault)
                continue;

            var resizedMask = ResizeMaskNearest(
                state.MaskData,
                state.Width,
                state.Height,
                targetWidth,
                targetHeight);
            result.Add((resizedMask, p));
        }

        return result;
    }

    private static AdjustmentParams BuildLayerParams(MaskLayerState layer) => new(
        Brightness: 1.0f + (float)(layer.Brightness / 100.0),
        Contrast: 1.0f + (float)(layer.Contrast / 100.0),
        Gamma: (float)layer.Gamma,
        Exposure: (float)layer.Exposure,
        Saturation: 1.0f + (float)(layer.Saturation / 100.0),
        Temperature: (float)layer.Temperature,
        Tint: (float)layer.Tint,
        Shadows: (float)layer.Shadows,
        Highlights: (float)layer.Highlights,
        Clarity: (float)layer.Clarity,
        Blur: (float)layer.Blur,
        Sharpen: (float)layer.Sharpen,
        Vignette: (float)layer.Vignette
    );

    private static AdjustmentParams BuildLayerParams(MaskAdjustmentValues values) => new(
        Brightness: 1.0f + (float)(values.Brightness / 100.0),
        Contrast: 1.0f + (float)(values.Contrast / 100.0),
        Gamma: (float)values.Gamma,
        Exposure: (float)values.Exposure,
        Saturation: 1.0f + (float)(values.Saturation / 100.0),
        Temperature: (float)values.Temperature,
        Tint: (float)values.Tint,
        Shadows: (float)values.Shadows,
        Highlights: (float)values.Highlights,
        Clarity: (float)values.Clarity,
        Blur: (float)values.Blur,
        Sharpen: (float)values.Sharpen,
        Vignette: (float)values.Vignette
    );

    private static byte[] ResizeMaskNearest(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
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

    private static byte[] BuildDilatedBinaryMask(byte[] source, int width, int height, int iterations)
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

    private void RotateMaskClockwise90()
    {
        lock (_maskSync)
        {
            foreach (var layer in MaskLayers)
            {
                if (layer.Width <= 0 || layer.Height <= 0) continue;
                int oldW = layer.Width;
                int oldH = layer.Height;
                int newW = oldH;
                int newH = oldW;
                var rotated = new byte[layer.MaskData.Length];

                for (int y = 0; y < oldH; y++)
                {
                    int srcRow = y * oldW;
                    for (int x = 0; x < oldW; x++)
                    {
                        int newX = oldH - 1 - y;
                        int newY = x;
                        rotated[newY * newW + newX] = layer.MaskData[srcRow + x];
                    }
                }

                layer.SetMaskData(rotated, newW, newH, layer.NonZeroCount);
            }
        }
    }

    private void RotateMaskCounterClockwise90()
    {
        lock (_maskSync)
        {
            foreach (var layer in MaskLayers)
            {
                if (layer.Width <= 0 || layer.Height <= 0) continue;
                int oldW = layer.Width;
                int oldH = layer.Height;
                int newW = oldH;
                int newH = oldW;
                var rotated = new byte[layer.MaskData.Length];

                for (int y = 0; y < oldH; y++)
                {
                    int srcRow = y * oldW;
                    for (int x = 0; x < oldW; x++)
                    {
                        int newX = y;
                        int newY = oldW - 1 - x;
                        rotated[newY * newW + newX] = layer.MaskData[srcRow + x];
                    }
                }

                layer.SetMaskData(rotated, newW, newH, layer.NonZeroCount);
            }
        }
    }

    private void FlipMaskHorizontal()
    {
        lock (_maskSync)
        {
            foreach (var layer in MaskLayers)
            {
                int w = layer.Width;
                int h = layer.Height;
                if (w <= 1 || h <= 0) continue;

                var data = layer.MaskData;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    int half = w / 2;
                    for (int x = 0; x < half; x++)
                    {
                        int left = row + x;
                        int right = row + (w - 1 - x);
                        (data[left], data[right]) = (data[right], data[left]);
                    }
                }
            }
        }
    }

    private void FlipMaskVertical()
    {
        lock (_maskSync)
        {
            foreach (var layer in MaskLayers)
            {
                int w = layer.Width;
                int h = layer.Height;
                if (w <= 0 || h <= 1) continue;

                var data = layer.MaskData;
                int half = h / 2;
                for (int y = 0; y < half; y++)
                {
                    int top = y * w;
                    int bottom = (h - 1 - y) * w;
                    for (int x = 0; x < w; x++)
                        (data[top + x], data[bottom + x]) = (data[bottom + x], data[top + x]);
                }
            }
        }
    }
}
