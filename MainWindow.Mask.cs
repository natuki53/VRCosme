using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using VRCosme.Models;
using VRCosme.Views;

namespace VRCosme;

public partial class MainWindow
{
    // ───── マスク状態 ─────
    private bool _isMaskLassoing;
    private readonly List<(double X, double Y)> _maskLassoPoints = [];
    private Path? _maskOutlinePath;
    private Path? _maskLassoPath;
    private MaskLayer? _maskOutlineCacheLayer;
    private int _maskOutlineCacheRevision = -1;
    private IReadOnlyList<BoundaryContour>? _maskOutlineCacheContours;
    private MaskAdjustmentsDialog? _maskAdjustmentsDialog;
    private MaskLayer? _maskAdjustmentsTargetLayer;
    private int _maskAdjustmentsTargetLayerIndex = -1;
    private bool _isMaskAdjustmentsSelectionValidationPending;
    private const double MinMaskLassoPointDistanceSq = 1.0;

    // ============================================================
    //  マスクオーバーレイ - 初期化
    // ============================================================

    private void InitMaskOverlayElements()
    {
        _maskOutlinePath = new Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(232, 236, 246)),
            StrokeThickness = 1.2,
            StrokeDashArray = new DoubleCollection { 1.5, 2.5 },
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 4,
                ShadowDepth = 0,
                Opacity = 0.55
            },
            Visibility = Visibility.Collapsed
        };

        _maskLassoPath = new Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(116, 193, 255)),
            StrokeThickness = 1.2,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 3,
                ShadowDepth = 0,
                Opacity = 0.4
            },
            Visibility = Visibility.Collapsed
        };

        MaskOutlineCanvas.Children.Add(_maskOutlinePath);
        MaskOutlineCanvas.Children.Add(_maskLassoPath);
    }

    // ============================================================
    //  マスクレイヤー操作
    // ============================================================

    private void MaskLayerAdjustButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not MaskLayer layer)
            return;

        if (!ReferenceEquals(ViewModel.SelectedMaskLayer, layer))
            ViewModel.SelectedMaskLayer = layer;

        OpenMaskAdjustmentsPopup();
        e.Handled = true;
    }

    private void RenameMaskLayer_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasSelectedMaskLayer) return;
        var current = ViewModel.GetSelectedMaskLayerName();
        var dialog = new MaskRenameDialog(current) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ViewModel.RenameSelectedMaskLayer(dialog.MaskName);
        }
    }

    private void AutoMaskSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AutoMaskSettingsDialog { Owner = this };
        dialog.ShowDialog();
    }

    // ============================================================
    //  マスク補正ダイアログ
    // ============================================================

    private void OpenMaskAdjustmentsPopup()
    {
        if (!ViewModel.HasSelectedMaskLayer) return;
        var selectedLayer = ViewModel.SelectedMaskLayer;
        if (selectedLayer == null) return;

        if (_maskAdjustmentsDialog != null)
        {
            int selectedIndex = ViewModel.MaskLayers.IndexOf(selectedLayer);
            if (selectedIndex >= 0 && IsMaskAdjustmentsTargetMatch(selectedLayer, selectedIndex))
            {
                _maskAdjustmentsDialog.Activate();
                _maskAdjustmentsDialog.Focus();
                return;
            }

            _maskAdjustmentsDialog.Close();
        }

        var layerName = ViewModel.GetSelectedMaskLayerName();
        var initial = ViewModel.GetSelectedMaskAdjustmentValues();
        var dialog = new MaskAdjustmentsDialog(layerName, initial) { Owner = this };

        _maskAdjustmentsDialog = dialog;
        _maskAdjustmentsTargetLayer = selectedLayer;
        _maskAdjustmentsTargetLayerIndex = ViewModel.MaskLayers.IndexOf(selectedLayer);

        dialog.ValuesChanged += MaskAdjustmentsDialog_ValuesChanged;
        dialog.Closed += MaskAdjustmentsDialog_Closed;
        dialog.Show();
        dialog.Activate();
    }

    private void MaskAdjustmentsDialog_ValuesChanged(MaskAdjustmentValues values)
    {
        var selectedLayer = ViewModel.SelectedMaskLayer;
        if (selectedLayer == null)
            return;

        int selectedIndex = ViewModel.MaskLayers.IndexOf(selectedLayer);
        if (selectedIndex < 0 || !IsMaskAdjustmentsTargetMatch(selectedLayer, selectedIndex))
            return;

        _maskAdjustmentsTargetLayer = selectedLayer;
        _maskAdjustmentsTargetLayerIndex = selectedIndex;

        ViewModel.PreviewSelectedMaskAdjustments(values);
    }

    private void MaskAdjustmentsDialog_Closed(object? sender, EventArgs e)
    {
        if (sender is not MaskAdjustmentsDialog dialog)
            return;

        dialog.ValuesChanged -= MaskAdjustmentsDialog_ValuesChanged;
        dialog.Closed -= MaskAdjustmentsDialog_Closed;

        var selectedLayer = ViewModel.SelectedMaskLayer;
        int selectedIndex = selectedLayer == null ? -1 : ViewModel.MaskLayers.IndexOf(selectedLayer);
        bool applyChanges = dialog.IsConfirmed
            && selectedLayer != null
            && selectedIndex >= 0
            && IsMaskAdjustmentsTargetMatch(selectedLayer, selectedIndex);

        _maskAdjustmentsDialog = null;
        _maskAdjustmentsTargetLayer = null;
        _maskAdjustmentsTargetLayerIndex = -1;
        _isMaskAdjustmentsSelectionValidationPending = false;

        if (applyChanges)
            ViewModel.ApplySelectedMaskAdjustments(dialog.Values);
        else
            ViewModel.ClearSelectedMaskAdjustmentsPreview();
    }

    private bool IsMaskAdjustmentsTargetMatch(MaskLayer selectedLayer, int selectedIndex)
    {
        bool sameReference = _maskAdjustmentsTargetLayer != null
            && ReferenceEquals(selectedLayer, _maskAdjustmentsTargetLayer);
        bool sameIndex = _maskAdjustmentsTargetLayerIndex >= 0
            && selectedIndex == _maskAdjustmentsTargetLayerIndex;
        return sameReference || sameIndex;
    }

    private void ScheduleMaskAdjustmentsDialogSelectionValidation()
    {
        if (_maskAdjustmentsDialog == null || _isMaskAdjustmentsSelectionValidationPending)
            return;

        _isMaskAdjustmentsSelectionValidationPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            _isMaskAdjustmentsSelectionValidationPending = false;
            ValidateMaskAdjustmentsDialogSelection();
        }));
    }

    private void ValidateMaskAdjustmentsDialogSelection()
    {
        if (_maskAdjustmentsDialog == null)
            return;

        var selectedLayer = ViewModel.SelectedMaskLayer;
        if (selectedLayer == null || !ViewModel.HasSelectedMaskLayer)
        {
            if (ViewModel.MaskLayers.Count == 0)
                _maskAdjustmentsDialog.Close();
            return;
        }

        int selectedIndex = ViewModel.MaskLayers.IndexOf(selectedLayer);
        if (selectedIndex < 0)
        {
            _maskAdjustmentsDialog.Close();
            return;
        }

        if (IsMaskAdjustmentsTargetMatch(selectedLayer, selectedIndex))
        {
            _maskAdjustmentsTargetLayer = selectedLayer;
            _maskAdjustmentsTargetLayerIndex = selectedIndex;
        }
        else
        {
            _maskAdjustmentsDialog.Close();
        }
    }

    // ============================================================
    //  ラッソ操作
    // ============================================================

    private void AddLassoPoint(Point imagePoint)
    {
        if (_maskLassoPoints.Count == 0)
        {
            _maskLassoPoints.Add((imagePoint.X, imagePoint.Y));
            return;
        }

        var last = _maskLassoPoints[^1];
        double dx = imagePoint.X - last.X;
        double dy = imagePoint.Y - last.Y;
        if ((dx * dx + dy * dy) < MinMaskLassoPointDistanceSq)
            return;

        _maskLassoPoints.Add((imagePoint.X, imagePoint.Y));
    }

    private void CompleteMaskLasso()
    {
        if (!_isMaskLassoing)
            return;

        _isMaskLassoing = false;
        if (_maskLassoPoints.Count >= 3)
            ViewModel.ApplyMaskLasso(_maskLassoPoints, ViewModel.IsMaskEraseMode);

        _maskLassoPoints.Clear();
        UpdateMaskLassoOverlay();
        UpdateMaskOutlineOverlay();

        if (PreviewArea.IsMouseCaptured)
            PreviewArea.ReleaseMouseCapture();
        if (!_isPanning)
            PreviewArea.Cursor = (ViewModel.IsMaskEditing || ViewModel.IsMaskEraseMode || ViewModel.IsMaskColorAutoSelectMode || ViewModel.IsMaskAutoSelectMode)
                && ViewModel.HasSelectedMaskLayer
                ? Cursors.Cross
                : null;
    }

    private void CancelMaskLasso()
    {
        if (!_isMaskLassoing && _maskLassoPoints.Count == 0)
            return;

        _isMaskLassoing = false;
        _maskLassoPoints.Clear();
        UpdateMaskLassoOverlay();
        if (PreviewArea.IsMouseCaptured && !_isPanning)
            PreviewArea.ReleaseMouseCapture();
        if (!_isPanning)
            PreviewArea.Cursor = (ViewModel.IsMaskEditing || ViewModel.IsMaskEraseMode || ViewModel.IsMaskColorAutoSelectMode || ViewModel.IsMaskAutoSelectMode)
                && ViewModel.HasSelectedMaskLayer
                ? Cursors.Cross
                : null;
    }

    // ============================================================
    //  マスクオーバーレイ描画
    // ============================================================

    private void UpdateMaskOutlineOverlay()
    {
        if (_maskOutlinePath == null)
            return;

        if (!ViewModel.HasImage || !ViewModel.HasSelectedMaskLayer || ViewModel.SelectedMaskLayer is null)
        {
            InvalidateMaskOutlineCache();
            _maskOutlinePath.Data = null;
            _maskOutlinePath.Visibility = Visibility.Collapsed;
            return;
        }

        var selectedLayer = ViewModel.SelectedMaskLayer;
        int revision = ViewModel.MaskRevision;
        if (!ReferenceEquals(_maskOutlineCacheLayer, selectedLayer) || _maskOutlineCacheRevision != revision)
        {
            var state = ViewModel.GetSelectedMaskLayerState();
            if (state == null || state.NonZeroCount <= 0 || state.Width <= 0 || state.Height <= 0)
            {
                InvalidateMaskOutlineCache();
                _maskOutlinePath.Data = null;
                _maskOutlinePath.Visibility = Visibility.Collapsed;
                return;
            }

            _maskOutlineCacheContours = BuildBoundaryContours(state.MaskData, state.Width, state.Height);
            _maskOutlineCacheLayer = selectedLayer;
            _maskOutlineCacheRevision = revision;
        }

        if (_maskOutlineCacheContours == null || _maskOutlineCacheContours.Count == 0)
        {
            _maskOutlinePath.Data = null;
            _maskOutlinePath.Visibility = Visibility.Collapsed;
            return;
        }

        var (ir, scale) = GetImageRenderInfo();
        if (ir.IsEmpty || scale <= 0)
        {
            _maskOutlinePath.Data = null;
            _maskOutlinePath.Visibility = Visibility.Collapsed;
            return;
        }

        ApplyMaskOutlineStyle(scale);
        var geometry = BuildMaskBoundaryGeometry(_maskOutlineCacheContours, ir, scale);
        _maskOutlinePath.Data = geometry;
        _maskOutlinePath.Visibility = geometry == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void InvalidateMaskOutlineCache()
    {
        _maskOutlineCacheLayer = null;
        _maskOutlineCacheRevision = -1;
        _maskOutlineCacheContours = null;
    }

    private void ApplyMaskOutlineStyle(double scale)
    {
        if (_maskOutlinePath == null) return;

        double zoom = Math.Max(1.0, scale);
        double dash = Math.Clamp(3.0 + Math.Log2(zoom), 3.0, 8.0);
        _maskOutlinePath.StrokeDashArray = new DoubleCollection { dash, dash };
        _maskOutlinePath.StrokeThickness = Math.Clamp(1.2 + Math.Log2(zoom) * 0.08, 1.2, 1.8);
    }

    private void UpdateMaskLassoOverlay()
    {
        if (_maskLassoPath == null)
            return;

        if (!_isMaskLassoing || _maskLassoPoints.Count < 2)
        {
            _maskLassoPath.Data = null;
            _maskLassoPath.Visibility = Visibility.Collapsed;
            return;
        }

        var (ir, scale) = GetImageRenderInfo();
        if (ir.IsEmpty || scale <= 0)
        {
            _maskLassoPath.Data = null;
            _maskLassoPath.Visibility = Visibility.Collapsed;
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var first = ToPreviewPoint(_maskLassoPoints[0], ir, scale);
            ctx.BeginFigure(first, false, true);
            for (int i = 1; i < _maskLassoPoints.Count; i++)
            {
                var point = ToPreviewPoint(_maskLassoPoints[i], ir, scale);
                ctx.LineTo(point, true, false);
            }
        }

        geometry.Freeze();
        _maskLassoPath.Data = geometry;
        _maskLassoPath.Visibility = Visibility.Visible;
    }

    // ============================================================
    //  マスク境界ジオメトリ
    // ============================================================

    private static StreamGeometry? BuildMaskBoundaryGeometry(
        IReadOnlyList<BoundaryContour> contours,
        Rect imageRect,
        double scale)
    {
        if (contours.Count == 0)
            return null;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            foreach (var contour in contours)
            {
                var points = contour.Points;
                if (points.Count < 2)
                    continue;

                var first = ToPreviewPoint(points[0], imageRect, scale);
                ctx.BeginFigure(first, false, contour.Closed);
                for (int i = 1; i < points.Count; i++)
                {
                    var p = ToPreviewPoint(points[i], imageRect, scale);
                    ctx.LineTo(p, true, false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static List<BoundaryContour> BuildBoundaryContours(byte[] data, int width, int height)
    {
        if (width <= 0 || height <= 0 || data.Length != width * height)
            return [];

        var edges = BuildBoundaryEdges(data, width, height);
        if (edges.Count == 0)
            return [];

        var remaining = new HashSet<GridEdge>(edges);
        var byStart = new Dictionary<GridPoint, List<GridEdge>>();
        foreach (var edge in edges)
        {
            if (!byStart.TryGetValue(edge.Start, out var list))
            {
                list = new List<GridEdge>(2);
                byStart[edge.Start] = list;
            }
            list.Add(edge);
        }

        var contours = new List<BoundaryContour>(Math.Max(8, edges.Count / 64));
        while (remaining.Count > 0)
        {
            var seed = remaining.First();
            RemoveEdge(seed, remaining, byStart);

            var points = new List<GridPoint>(64) { seed.Start, seed.End };
            var start = seed.Start;
            var current = seed.End;
            bool closed = false;

            while (TryTakeEdgeStartingAt(current, remaining, byStart, out var next))
            {
                current = next.End;
                if (current == start)
                {
                    closed = true;
                    break;
                }

                points.Add(current);
            }

            var simplified = SimplifyOrthogonalPoints(points, closed);
            if (simplified.Count < 2)
                continue;

            contours.Add(new BoundaryContour(simplified, closed));
        }

        return contours;
    }

    private static List<GridEdge> BuildBoundaryEdges(byte[] data, int w, int h)
    {
        var edges = new List<GridEdge>(Math.Max(256, w + h));
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = row + x;
                if (data[idx] == 0) continue;

                if (y == 0 || data[idx - w] == 0)
                    edges.Add(new GridEdge(new GridPoint(x, y), new GridPoint(x + 1, y))); // top
                if (x == w - 1 || data[idx + 1] == 0)
                    edges.Add(new GridEdge(new GridPoint(x + 1, y), new GridPoint(x + 1, y + 1))); // right
                if (y == h - 1 || data[idx + w] == 0)
                    edges.Add(new GridEdge(new GridPoint(x + 1, y + 1), new GridPoint(x, y + 1))); // bottom
                if (x == 0 || data[idx - 1] == 0)
                    edges.Add(new GridEdge(new GridPoint(x, y + 1), new GridPoint(x, y))); // left
            }
        }

        return edges;
    }

    private static bool TryTakeEdgeStartingAt(
        GridPoint start,
        HashSet<GridEdge> remaining,
        Dictionary<GridPoint, List<GridEdge>> byStart,
        out GridEdge edge)
    {
        edge = default;
        if (!byStart.TryGetValue(start, out var list) || list.Count == 0)
            return false;

        edge = list[^1];
        RemoveEdge(edge, remaining, byStart);
        return true;
    }

    private static void RemoveEdge(
        GridEdge edge,
        HashSet<GridEdge> remaining,
        Dictionary<GridPoint, List<GridEdge>> byStart)
    {
        if (!remaining.Remove(edge))
            return;

        if (!byStart.TryGetValue(edge.Start, out var list))
            return;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Equals(edge))
            {
                list.RemoveAt(i);
                break;
            }
        }

        if (list.Count == 0)
            byStart.Remove(edge.Start);
    }

    private static List<GridPoint> SimplifyOrthogonalPoints(IReadOnlyList<GridPoint> points, bool closed)
    {
        if (points.Count <= 2)
            return points.ToList();

        var simplified = new List<GridPoint>(points.Count) { points[0] };
        for (int i = 1; i < points.Count - 1; i++)
        {
            var prev = simplified[^1];
            var curr = points[i];
            var next = points[i + 1];
            bool collinear = (prev.X == curr.X && curr.X == next.X)
                             || (prev.Y == curr.Y && curr.Y == next.Y);
            if (!collinear)
                simplified.Add(curr);
        }

        simplified.Add(points[^1]);

        if (closed && simplified.Count >= 3)
        {
            var first = simplified[0];
            var last = simplified[^1];
            var prev = simplified[^2];
            bool redundantLast = (prev.X == last.X && last.X == first.X)
                                 || (prev.Y == last.Y && last.Y == first.Y);
            if (redundantLast)
                simplified.RemoveAt(simplified.Count - 1);
        }

        return simplified;
    }

    // ============================================================
    //  座標変換ヘルパー & 型定義
    // ============================================================

    private static Point ToPreviewPoint((double X, double Y) point, Rect imageRect, double scale) =>
        new(imageRect.X + point.X * scale, imageRect.Y + point.Y * scale);

    private static Point ToPreviewPoint(GridPoint point, Rect imageRect, double scale) =>
        new(imageRect.X + point.X * scale, imageRect.Y + point.Y * scale);

    private readonly record struct BoundaryContour(IReadOnlyList<GridPoint> Points, bool Closed);
    private readonly record struct GridPoint(int X, int Y);
    private readonly record struct GridEdge(GridPoint Start, GridPoint End);
}
