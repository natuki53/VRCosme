using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VRCosme.ViewModels;

namespace VRCosme;

public partial class MainWindow
{
    // ───── ズーム & パン ─────
    private double _zoomLevel = 1.0;
    private bool _isPanning;
    private Point _lastPanPoint;
    private MouseButton _panButton;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 50.0;
    private const double WheelZoomFactor = 1.15;

    // ───── ツールバードラッグ ─────
    private bool _isToolbarDragging;
    private Point _toolbarDragStart;

    // ============================================================
    //  ズーム & パン
    // ============================================================

    private void PreviewArea_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!ViewModel.HasImage) return;

        var mousePos = e.GetPosition(PreviewArea);
        double oldZoom = _zoomLevel;

        if (e.Delta > 0)
            _zoomLevel = Math.Min(_zoomLevel * WheelZoomFactor, MaxZoom);
        else
            _zoomLevel = Math.Max(_zoomLevel / WheelZoomFactor, MinZoom);

        ApplyZoomAtPoint(oldZoom, _zoomLevel, mousePos);
        e.Handled = true;
    }

    private async void PreviewArea_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ViewModel.HasImage) return;

        // ツールバー上のクリックはパンしない
        if (IsDescendantOf(e.OriginalSource as DependencyObject, FloatingToolbar))
            return;

        if (ViewModel.IsMaskAutoSelectMode && ViewModel.HasSelectedMaskLayer && e.ChangedButton == MouseButton.Left)
        {
            if (TryMapPreviewToImage(e.GetPosition(PreviewArea), out var imagePoint, out var insideImage) && insideImage)
            {
                e.Handled = true;
                await ViewModel.AutoSelectMaskAtAsync(imagePoint.X, imagePoint.Y);
                UpdateMaskOutlineOverlay();
            }
            return;
        }

        // マスク編集モード時の左ドラッグはラッソ描画に使用
        if (ViewModel.IsMaskEditing && ViewModel.HasSelectedMaskLayer && e.ChangedButton == MouseButton.Left)
        {
            if (TryMapPreviewToImage(e.GetPosition(PreviewArea), out var imagePoint, out var insideImage) && insideImage)
            {
                _isMaskLassoing = true;
                _maskLassoPoints.Clear();
                _maskLassoPoints.Add((imagePoint.X, imagePoint.Y));
                PreviewArea.CaptureMouse();
                PreviewArea.Cursor = Cursors.Cross;
                UpdateMaskLassoOverlay();

                e.Handled = true;
            }
            return;
        }

        // 中クリック → 常にパン
        if (e.ChangedButton == MouseButton.Middle && _dragMode == DragMode.None)
        {
            StartPan(e);
        }
        // 左クリック → トリミング非アクティブ時のみパン (分割線付近は除外)
        else if (e.ChangedButton == MouseButton.Left
                 && !ViewModel.IsCropActive
                 && !ViewModel.IsMaskEditing
                 && !ViewModel.IsMaskAutoSelectMode
                 && _dragMode == DragMode.None
                 && !_isPanning
                 && !IsNearSplitLine(e.GetPosition(PreviewArea)))
        {
            StartPan(e);
        }
    }

    private void PreviewArea_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isMaskLassoing)
        {
            if (TryMapPreviewToImage(e.GetPosition(PreviewArea), out var imagePoint, out _))
            {
                AddLassoPoint(imagePoint);
                UpdateMaskLassoOverlay();
            }

            e.Handled = true;
            return;
        }

        if ((ViewModel.IsMaskEditing || ViewModel.IsMaskAutoSelectMode) && ViewModel.HasSelectedMaskLayer && !_isPanning)
            PreviewArea.Cursor = Cursors.Cross;

        if (!_isPanning) return;

        var pos = e.GetPosition(PreviewArea);
        PanTransform.X += pos.X - _lastPanPoint.X;
        PanTransform.Y += pos.Y - _lastPanPoint.Y;
        _lastPanPoint = pos;

        UpdateAllOverlays();
        e.Handled = true;
    }

    private void PreviewArea_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isMaskLassoing && e.ChangedButton == MouseButton.Left)
        {
            CompleteMaskLasso();
            e.Handled = true;
            return;
        }

        if (_isPanning && e.ChangedButton == _panButton)
        {
            _isPanning = false;
            PreviewArea.ReleaseMouseCapture();
            PreviewArea.Cursor = (ViewModel.IsMaskEditing || ViewModel.IsMaskAutoSelectMode) && ViewModel.HasSelectedMaskLayer
                ? Cursors.Cross
                : null;
            e.Handled = true;
        }
    }

    private void StartPan(MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panButton = e.ChangedButton;
        _lastPanPoint = e.GetPosition(PreviewArea);
        PreviewArea.CaptureMouse();
        PreviewArea.Cursor = Cursors.Hand;
        e.Handled = true;
    }

    private void ApplyZoomAtPoint(double oldZoom, double newZoom, Point fixedPoint)
    {
        double ratio = newZoom / oldZoom;

        PanTransform.X = fixedPoint.X - ratio * (fixedPoint.X - PanTransform.X);
        PanTransform.Y = fixedPoint.Y - ratio * (fixedPoint.Y - PanTransform.Y);

        ZoomTransform.ScaleX = newZoom;
        ZoomTransform.ScaleY = newZoom;

        UpdateZoomDisplay();
        UpdateAllOverlays();
    }

    private void ResetZoomPan()
    {
        _zoomLevel = 1.0;
        ZoomTransform.ScaleX = 1.0;
        ZoomTransform.ScaleY = 1.0;
        PanTransform.X = 0;
        PanTransform.Y = 0;
        UpdateZoomDisplay();
        UpdateAllOverlays();
    }

    private void ZoomByStep(bool zoomIn)
    {
        double oldZoom = _zoomLevel;
        if (zoomIn)
            _zoomLevel = Math.Min(_zoomLevel * 1.25, MaxZoom);
        else
            _zoomLevel = Math.Max(_zoomLevel / 1.25, MinZoom);

        var center = new Point(PreviewArea.ActualWidth / 2, PreviewArea.ActualHeight / 2);
        ApplyZoomAtPoint(oldZoom, _zoomLevel, center);
    }

    private void UpdateZoomDisplay()
    {
        if (PreviewImage.Source is not BitmapSource source || PreviewImage.ActualWidth <= 0)
        {
            ZoomLevelText.Text = "—";
            return;
        }

        double cw = PreviewImage.ActualWidth, ch = PreviewImage.ActualHeight;
        double imgA = (double)source.PixelWidth / source.PixelHeight;
        double ctlA = cw / ch;
        double fittedW = imgA > ctlA ? cw : ch * imgA;

        double effectivePercent = _zoomLevel * fittedW / ViewModel.ImageWidth * 100;
        ZoomLevelText.Text = $"{effectivePercent:F0}%";
    }

    // ── ツールバーボタン ──

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomByStep(true);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomByStep(false);

    // ── ツールバードラッグ ──

    private void ToolbarGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isToolbarDragging = true;
        _toolbarDragStart = e.GetPosition(PreviewArea);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void ToolbarGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isToolbarDragging) return;
        var pos = e.GetPosition(PreviewArea);
        ToolbarTranslate.X += pos.X - _toolbarDragStart.X;
        ToolbarTranslate.Y += pos.Y - _toolbarDragStart.Y;
        _toolbarDragStart = pos;

        ClampToolbarPosition();
        e.Handled = true;
    }

    /// <summary>ツールバーをプレビューエリア内に収める</summary>
    private void ClampToolbarPosition()
    {
        double areaW = PreviewArea.ActualWidth;
        double areaH = PreviewArea.ActualHeight;
        double tbW = FloatingToolbar.ActualWidth;
        double tbH = FloatingToolbar.ActualHeight;
        if (areaW <= 0 || tbW <= 0) return;

        // ツールバーの基準位置 (HorizontalAlignment=Center, VerticalAlignment=Bottom, Margin bottom=14)
        double baseX = (areaW - tbW) / 2;
        double baseY = areaH - tbH - 14;

        // Translate の範囲を計算してクランプ
        double minTx = -baseX;
        double maxTx = areaW - tbW - baseX;
        double minTy = -baseY;
        double maxTy = 14; // bottom margin 分だけ下に余裕

        ToolbarTranslate.X = Math.Clamp(ToolbarTranslate.X, minTx, maxTx);
        ToolbarTranslate.Y = Math.Clamp(ToolbarTranslate.Y, minTy, maxTy);
    }

    private void ToolbarGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isToolbarDragging) return;
        _isToolbarDragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }
}
