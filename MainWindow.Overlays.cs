using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VRCosme.ViewModels;

namespace VRCosme;

public partial class MainWindow
{
    // ───── 分割線要素 ─────
    private Line? _splitLine;
    private Rectangle? _splitDragHandle;
    private Border? _splitKnob;
    private bool _isSplitDragging;

    // ───── 三分割法グリッド線 ─────
    private readonly Line[] _gridLines = new Line[4];

    // ============================================================
    //  比較モード
    // ============================================================

    private void OnCompareModeChanged()
    {
        switch (ViewModel.CompareMode)
        {
            case CompareMode.After:
                BeforePreviewImage.Visibility = Visibility.Collapsed;
                BeforePreviewImage.Clip = null;
                SplitLineCanvas.Visibility = Visibility.Collapsed;
                break;

            case CompareMode.Before:
                BeforePreviewImage.Visibility = Visibility.Visible;
                BeforePreviewImage.Clip = null;
                SplitLineCanvas.Visibility = Visibility.Collapsed;
                break;

            case CompareMode.Split:
                BeforePreviewImage.Visibility = Visibility.Visible;
                SplitLineCanvas.Visibility = Visibility.Visible;
                UpdateSplitView();
                break;
        }
    }

    private void InitSplitLine()
    {
        _splitLine = new Line
        {
            Stroke = Brushes.White,
            StrokeThickness = 2,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 4, ShadowDepth = 0, Opacity = 0.6
            }
        };
        SplitLineCanvas.Children.Add(_splitLine);

        // ドラッグ用の透明ヒットエリア (分割線の左右に幅を持たせる)
        _splitDragHandle = new Rectangle
        {
            Width = 20,
            Fill = Brushes.Transparent,
            Cursor = Cursors.SizeWE,
            IsHitTestVisible = true
        };
        _splitDragHandle.MouseLeftButtonDown += SplitDragHandle_MouseLeftButtonDown;
        _splitDragHandle.MouseMove += SplitDragHandle_MouseMove;
        _splitDragHandle.MouseLeftButtonUp += SplitDragHandle_MouseLeftButtonUp;
        SplitLineCanvas.Children.Add(_splitDragHandle);

        // 分割線中央のドラッグノブ (視覚的インジケーター)
        _splitKnob = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "◀ ▶",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.4
            }
        };
        SplitLineCanvas.Children.Add(_splitKnob);
    }

    private void UpdateSplitView()
    {
        if (ViewModel.CompareMode != CompareMode.Split) return;

        double w = BeforePreviewImage.ActualWidth;
        double h = BeforePreviewImage.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double splitFraction = ViewModel.SplitPosition;
        double splitX = w * splitFraction;

        // Before画像を左半分にクリップ (左=Before, 右=After)
        BeforePreviewImage.Clip = new RectangleGeometry(new Rect(0, 0, splitX, h));

        // 分割線を配置
        if (_splitLine != null)
        {
            var offset = BeforePreviewImage.TranslatePoint(new Point(splitX, 0), SplitLineCanvas);
            double canvasH = SplitLineCanvas.ActualHeight;

            _splitLine.X1 = offset.X;
            _splitLine.Y1 = 0;
            _splitLine.X2 = offset.X;
            _splitLine.Y2 = canvasH;

            // ドラッグハンドルを分割線の位置に配置
            if (_splitDragHandle != null)
            {
                double handleW = _splitDragHandle.Width;
                Canvas.SetLeft(_splitDragHandle, offset.X - handleW / 2);
                Canvas.SetTop(_splitDragHandle, 0);
                _splitDragHandle.Height = canvasH;
            }

            // ノブを分割線の中央に配置
            if (_splitKnob != null)
            {
                double knobW = _splitKnob.Width;
                double knobH = _splitKnob.Height;
                Canvas.SetLeft(_splitKnob, offset.X - knobW / 2);
                Canvas.SetTop(_splitKnob, canvasH / 2 - knobH / 2);
            }
        }
    }

    // ============================================================
    //  分割線ドラッグ
    // ============================================================

    private void SplitDragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSplitDragging = true;
        _splitDragHandle?.CaptureMouse();
        e.Handled = true;
    }

    private void SplitDragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSplitDragging) return;

        // SplitLineCanvas 上のマウス位置 → BeforePreviewImage 座標系に変換
        var canvasPos = e.GetPosition(SplitLineCanvas);
        var imagePoint = SplitLineCanvas.TranslatePoint(canvasPos, BeforePreviewImage);
        double w = BeforePreviewImage.ActualWidth;
        if (w <= 0) return;

        double fraction = Math.Clamp(imagePoint.X / w, 0.0, 1.0);
        ViewModel.SplitPosition = fraction;
        UpdateSplitView();
        e.Handled = true;
    }

    private void SplitDragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSplitDragging)
        {
            _isSplitDragging = false;
            _splitDragHandle?.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private bool IsNearSplitLine(Point previewPos)
    {
        if (ViewModel.CompareMode != CompareMode.Split
            || _splitLine == null
            || SplitLineCanvas.Visibility != Visibility.Visible)
            return false;
        return Math.Abs(previewPos.X - _splitLine.X1) < 14;
    }

    // ============================================================
    //  三分割法グリッド線
    // ============================================================

    private void InitGridOverlay()
    {
        var gridBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255));
        gridBrush.Freeze();
        var gridStrokeThickness = 1.0;
        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 2,
            ShadowDepth = 0,
            Opacity = 0.5
        };

        for (int i = 0; i < 4; i++)
        {
            _gridLines[i] = new Line
            {
                Stroke = gridBrush,
                StrokeThickness = gridStrokeThickness,
                IsHitTestVisible = false,
                Effect = shadow
            };
            GridOverlayCanvas.Children.Add(_gridLines[i]);
        }
    }

    private void UpdateGridOverlay()
    {
        if (!ViewModel.ShowRuleOfThirdsGrid || !ViewModel.HasImage)
        {
            GridOverlayCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        // トリミング中は選択中のアスペクト比の枠内でグリッドを表示
        var (ir, _) = GetImageRenderInfo();
        Rect rect = ViewModel.IsCropActive ? GetCropScreenRect() : ir;
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            GridOverlayCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        double x1 = rect.X;
        double x2 = rect.X + rect.Width;
        double y1 = rect.Y;
        double y2 = rect.Y + rect.Height;
        double v1 = rect.X + rect.Width / 3;
        double v2 = rect.X + rect.Width * 2 / 3;
        double h1 = rect.Y + rect.Height / 3;
        double h2 = rect.Y + rect.Height * 2 / 3;

        // 縦線 2 本
        _gridLines[0].X1 = v1; _gridLines[0].Y1 = y1; _gridLines[0].X2 = v1; _gridLines[0].Y2 = y2;
        _gridLines[1].X1 = v2; _gridLines[1].Y1 = y1; _gridLines[1].X2 = v2; _gridLines[1].Y2 = y2;
        // 横線 2 本
        _gridLines[2].X1 = x1; _gridLines[2].Y1 = h1; _gridLines[2].X2 = x2; _gridLines[2].Y2 = h1;
        _gridLines[3].X1 = x1; _gridLines[3].Y1 = h2; _gridLines[3].X2 = x2; _gridLines[3].Y2 = h2;

        GridOverlayCanvas.Visibility = Visibility.Visible;
    }

    // ============================================================
    //  ルーラー
    // ============================================================

    private static Brush GetRulerBrush(FrameworkElement element)
    {
        var brush = element.TryFindResource("LabelForegroundBrush") as Brush;
        return brush ?? new SolidColorBrush(Color.FromRgb(100, 100, 100));
    }

    private void UpdateRulerOverlay()
    {
        if (!ViewModel.ShowRuler || !ViewModel.HasImage)
        {
            RulerHorizontal.Children.Clear();
            RulerVertical.Children.Clear();
            return;
        }

        var (ir, scale) = GetImageRenderInfo();
        if (ir.IsEmpty || scale <= 0)
        {
            RulerHorizontal.Children.Clear();
            RulerVertical.Children.Clear();
            return;
        }

        var brush = GetRulerBrush(RulerHorizontal);
        double rulerH = RulerHorizontal.ActualHeight;
        double rulerW = RulerVertical.ActualWidth;
        if (rulerH <= 0 || rulerW <= 0) return;

        int imgW = ViewModel.ImageWidth;
        int imgH = ViewModel.ImageHeight;

        // 目盛り間隔: スケールに応じて適切な間隔を選択（画面上でおおよそ 40～80px）
        int step = scale >= 2 ? 1 : scale >= 1 ? 5 : scale >= 0.5 ? 10 : scale >= 0.2 ? 25 : scale >= 0.1 ? 50 : 100;
        while (step * scale < 35 && step < 5000) step = step < 5 ? step + 1 : step < 20 ? step + 5 : step + 10;

        // 水平ルーラー
        RulerHorizontal.Children.Clear();
        for (int p = 0; p <= imgW; p += step)
        {
            double x = ir.X + p * scale;
            if (x < -20 || x > RulerHorizontal.ActualWidth + 20) continue;
            bool major = (p % (step * 5) == 0) || (step >= 50 && p % step == 0);
            double tickLen = major ? 8 : 4;
            var line = new Line
            {
                X1 = x, Y1 = rulerH,
                X2 = x, Y2 = rulerH - tickLen,
                Stroke = brush, StrokeThickness = 1,
                IsHitTestVisible = false
            };
            RulerHorizontal.Children.Add(line);
            if (major && p % (step * 2) == 0)
            {
                var tb = new TextBlock
                {
                    Text = p.ToString(),
                    FontSize = 10,
                    Foreground = brush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(tb, x - 5);
                Canvas.SetTop(tb, 2);
                RulerHorizontal.Children.Add(tb);
            }
        }

        // 垂直ルーラー
        RulerVertical.Children.Clear();
        double vRulerHeight = RulerVertical.ActualHeight;
        for (int p = 0; p <= imgH; p += step)
        {
            double y = ir.Y + p * scale;
            if (y < -20 || y > vRulerHeight + 20) continue;
            bool major = (p % (step * 5) == 0) || (step >= 50 && p % step == 0);
            double tickLen = major ? 8 : 4;
            var line = new Line
            {
                X1 = 0, Y1 = y,
                X2 = tickLen, Y2 = y,
                Stroke = brush, StrokeThickness = 1,
                IsHitTestVisible = false
            };
            RulerVertical.Children.Add(line);
            if (major && p % (step * 2) == 0)
            {
                var tb = new TextBlock
                {
                    Text = p.ToString(),
                    FontSize = 10,
                    Foreground = brush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(tb, 2);
                Canvas.SetTop(tb, y - 6);
                RulerVertical.Children.Add(tb);
            }
        }
    }
}
