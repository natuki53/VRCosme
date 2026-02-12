using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VRCosme;

public partial class MainWindow
{
    // ───── トリミングオーバーレイ要素 ─────
    private Path? _dimOverlay;
    private Rectangle? _cropBorder;
    private readonly Ellipse[] _handles = new Ellipse[4];

    // ───── ドラッグ状態 ─────
    private enum DragMode { None, Move, ResizeNW, ResizeNE, ResizeSE, ResizeSW }
    private DragMode _dragMode = DragMode.None;
    private Point _dragStartScreen;
    private double _startCropX, _startCropY, _startCropW, _startCropH;
    private const double HandleRadius = 6;
    private const double HandleHitRadius = 14;

    // ============================================================
    //  トリミングオーバーレイ - 初期化 & 描画
    // ============================================================

    private void InitCropOverlayElements()
    {
        _dimOverlay = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
            IsHitTestVisible = false
        };
        CropCanvas.Children.Add(_dimOverlay);

        _cropBorder = new Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        CropCanvas.Children.Add(_cropBorder);

        var accent = new SolidColorBrush(Color.FromRgb(59, 130, 246));
        for (int i = 0; i < 4; i++)
        {
            _handles[i] = new Ellipse
            {
                Width = HandleRadius * 2, Height = HandleRadius * 2,
                Fill = Brushes.White, Stroke = accent, StrokeThickness = 2,
                IsHitTestVisible = false
            };
            CropCanvas.Children.Add(_handles[i]);
        }
    }

    private void UpdateCropOverlay()
    {
        if (!ViewModel.IsCropActive || _dimOverlay == null || _cropBorder == null)
        {
            SetOverlayVisibility(Visibility.Collapsed);
            return;
        }

        var (ir, scale) = GetImageRenderInfo();
        if (ir.IsEmpty || scale <= 0) return;

        double sx = ir.X + ViewModel.CropX * scale;
        double sy = ir.Y + ViewModel.CropY * scale;
        double sw = Math.Max(1, ViewModel.CropWidth * scale);
        double sh = Math.Max(1, ViewModel.CropHeight * scale);
        double canvasW = CropCanvas.ActualWidth, canvasH = CropCanvas.ActualHeight;
        if (canvasW <= 0 || canvasH <= 0) return;

        _dimOverlay.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, canvasW, canvasH)),
            new RectangleGeometry(new Rect(sx, sy, sw, sh)));
        _dimOverlay.Visibility = Visibility.Visible;

        Canvas.SetLeft(_cropBorder, sx); Canvas.SetTop(_cropBorder, sy);
        _cropBorder.Width = sw; _cropBorder.Height = sh;
        _cropBorder.Visibility = Visibility.Visible;

        PlaceHandle(0, sx, sy);
        PlaceHandle(1, sx + sw, sy);
        PlaceHandle(2, sx + sw, sy + sh);
        PlaceHandle(3, sx, sy + sh);
    }

    private void PlaceHandle(int i, double cx, double cy)
    {
        Canvas.SetLeft(_handles[i], cx - HandleRadius);
        Canvas.SetTop(_handles[i], cy - HandleRadius);
        _handles[i].Visibility = Visibility.Visible;
    }

    private void SetOverlayVisibility(Visibility v)
    {
        if (_dimOverlay != null) _dimOverlay.Visibility = v;
        if (_cropBorder != null) _cropBorder.Visibility = v;
        foreach (var h in _handles) h?.SetValue(VisibilityProperty, v);
    }

    // ============================================================
    //  トリミング操作 - マウスイベント
    // ============================================================

    private void CropCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ViewModel.IsCropActive) return;
        var pos = e.GetPosition(CropCanvas);
        var cropScreen = GetCropScreenRect();
        _dragMode = HitTest(pos, cropScreen);

        if (_dragMode != DragMode.None)
        {
            ViewModel.PushUndoSnapshot();
            _dragStartScreen = pos;
            _startCropX = ViewModel.CropX;  _startCropY = ViewModel.CropY;
            _startCropW = ViewModel.CropWidth; _startCropH = ViewModel.CropHeight;
            CropCanvas.CaptureMouse();
        }
    }

    private void CropCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!ViewModel.IsCropActive) return;
        var pos = e.GetPosition(CropCanvas);

        if (_dragMode == DragMode.None)
        {
            var cropScreen = GetCropScreenRect();
            var hit = HitTest(pos, cropScreen);
            CropCanvas.Cursor = hit switch
            {
                DragMode.ResizeNW or DragMode.ResizeSE => Cursors.SizeNWSE,
                DragMode.ResizeNE or DragMode.ResizeSW => Cursors.SizeNESW,
                DragMode.Move => Cursors.SizeAll,
                _ => Cursors.Arrow
            };
            return;
        }

        var (_, scale) = GetImageRenderInfo();
        if (scale <= 0) return;
        double dx = (pos.X - _dragStartScreen.X) / scale;
        double dy = (pos.Y - _dragStartScreen.Y) / scale;

        if (_dragMode == DragMode.Move) ApplyMove(dx, dy);
        else ApplyResize(dx, dy, _dragMode);

        UpdateCropOverlay();
        UpdateGridOverlay();
    }

    private void CropCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode != DragMode.None) { _dragMode = DragMode.None; CropCanvas.ReleaseMouseCapture(); }
    }

    // ============================================================
    //  トリミング操作 - ヒットテスト & 移動/リサイズ
    // ============================================================

    private Rect GetCropScreenRect()
    {
        var (ir, scale) = GetImageRenderInfo();
        if (ir.IsEmpty) return Rect.Empty;
        return new Rect(
            ir.X + ViewModel.CropX * scale, ir.Y + ViewModel.CropY * scale,
            Math.Max(1, ViewModel.CropWidth * scale), Math.Max(1, ViewModel.CropHeight * scale));
    }

    private static DragMode HitTest(Point pos, Rect crop)
    {
        if (crop.IsEmpty) return DragMode.None;
        double r = HandleHitRadius;
        if (Dist(pos, crop.TopLeft) < r) return DragMode.ResizeNW;
        if (Dist(pos, crop.TopRight) < r) return DragMode.ResizeNE;
        if (Dist(pos, crop.BottomRight) < r) return DragMode.ResizeSE;
        if (Dist(pos, crop.BottomLeft) < r) return DragMode.ResizeSW;
        if (crop.Contains(pos)) return DragMode.Move;
        return DragMode.None;
    }

    private static double Dist(Point a, Point b)
    { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }

    private void ApplyMove(double dx, double dy)
    {
        ViewModel.CropX = Math.Clamp(_startCropX + dx, 0, ViewModel.ImageWidth - ViewModel.CropWidth);
        ViewModel.CropY = Math.Clamp(_startCropY + dy, 0, ViewModel.ImageHeight - ViewModel.CropHeight);
    }

    private void ApplyResize(double dx, double dy, DragMode mode)
    {
        double x = _startCropX, y = _startCropY, w = _startCropW, h = _startCropH;
        var ratio = ViewModel.SelectedCropRatio;
        bool lockAspect = !ratio.IsFree;
        double aspect = ratio.AspectRatio;

        double fixedX, fixedY, newW, newH;
        switch (mode)
        {
            case DragMode.ResizeNW: fixedX = x + w; fixedY = y + h; newW = w - dx; newH = h - dy; break;
            case DragMode.ResizeNE: fixedX = x;     fixedY = y + h; newW = w + dx; newH = h - dy; break;
            case DragMode.ResizeSE: fixedX = x;     fixedY = y;     newW = w + dx; newH = h + dy; break;
            case DragMode.ResizeSW: fixedX = x + w; fixedY = y;     newW = w - dx; newH = h + dy; break;
            default: return;
        }

        newW = Math.Max(newW, 20); newH = Math.Max(newH, 20);

        if (lockAspect && aspect > 0)
        {
            if (newW / newH > aspect) newW = newH * aspect;
            else newH = newW / aspect;
        }

        double newX, newY;
        switch (mode)
        {
            case DragMode.ResizeNW: newX = fixedX - newW; newY = fixedY - newH; break;
            case DragMode.ResizeNE: newX = fixedX;        newY = fixedY - newH; break;
            case DragMode.ResizeSE: newX = fixedX;        newY = fixedY;        break;
            case DragMode.ResizeSW: newX = fixedX - newW; newY = fixedY;        break;
            default: return;
        }

        if (newX < 0) { newW += newX; newX = 0; }
        if (newY < 0) { newH += newY; newY = 0; }
        if (newX + newW > ViewModel.ImageWidth) newW = ViewModel.ImageWidth - newX;
        if (newY + newH > ViewModel.ImageHeight) newH = ViewModel.ImageHeight - newY;

        if (lockAspect && aspect > 0)
        {
            if (newW / newH > aspect) newW = newH * aspect;
            else newH = newW / aspect;
        }

        ViewModel.CropX = newX; ViewModel.CropY = newY;
        ViewModel.CropWidth = Math.Max(newW, 1); ViewModel.CropHeight = Math.Max(newH, 1);
    }
}
