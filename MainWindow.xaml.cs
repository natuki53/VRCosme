using System.Linq;
using System.Collections.Generic;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using VRCosme.Models;
using VRCosme.Services;
using VRCosme.ViewModels;
using VRCosme.Views;

namespace VRCosme;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    // ───── トリミングオーバーレイ要素 ─────
    private Path? _dimOverlay;
    private Rectangle? _cropBorder;
    private readonly Ellipse[] _handles = new Ellipse[4];

    // ───── 分割線要素 ─────
    private Line? _splitLine;
    private Rectangle? _splitDragHandle;
    private Border? _splitKnob;
    private bool _isSplitDragging;

    // ───── 三分割法グリッド線 ─────
    private readonly Line[] _gridLines = new Line[4];

    // ───── ドラッグ状態 ─────
    private enum DragMode { None, Move, ResizeNW, ResizeNE, ResizeSE, ResizeSW }
    private DragMode _dragMode = DragMode.None;
    private Point _dragStartScreen;
    private double _startCropX, _startCropY, _startCropW, _startCropH;
    private const double HandleRadius = 6;
    private const double HandleHitRadius = 14;

    // ───── ズーム & パン ─────
    private double _zoomLevel = 1.0;
    private bool _isPanning;
    private Point _lastPanPoint;
    private MouseButton _panButton;
    private bool _isMaskLassoing;
    private readonly List<(double X, double Y)> _maskLassoPoints = [];
    private Path? _maskOutlinePath;
    private Path? _maskLassoPath;
    private MaskAdjustmentsDialog? _maskAdjustmentsDialog;
    private MaskLayer? _maskAdjustmentsTargetLayer;
    private int _maskAdjustmentsTargetLayerIndex = -1;
    private bool _isMaskAdjustmentsSelectionValidationPending;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 50.0;
    private const double WheelZoomFactor = 1.15;
    private const double MinMaskLassoPointDistanceSq = 1.0;

    private const double RightPanelMinWidth = 220;
    private const double RightPanelMaxWidth = 600;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += MainWindow_Closing;
        PreviewImage.SizeChanged += (_, _) => { OnImageLayoutChanged(); UpdateZoomDisplay(); };
        BeforePreviewImage.SizeChanged += (_, _) => OnImageLayoutChanged();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var savedWidth = ThemeService.GetRightPanelWidth();
        var width = Math.Clamp(savedWidth, RightPanelMinWidth, RightPanelMaxWidth);
        RightPanelColumn.Width = new GridLength(width);

        InitCropOverlayElements();
        InitSplitLine();
        InitGridOverlay();
        InitMaskOverlayElements();
        UpdateThemeMenuCheckmarks();

        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.IsCropActive)
                or nameof(MainViewModel.CropX) or nameof(MainViewModel.CropY)
                or nameof(MainViewModel.CropWidth) or nameof(MainViewModel.CropHeight))
            {
                UpdateCropOverlay();
                UpdateGridOverlay();
            }

            if (args.PropertyName == nameof(MainViewModel.ShowRuleOfThirdsGrid))
                UpdateGridOverlay();

            if (args.PropertyName == nameof(MainViewModel.ShowRuler))
                UpdateRulerOverlay();

            // 新しい画像読み込み時にズーム・パンをリセット
            if (args.PropertyName == nameof(MainViewModel.SourceFilePath))
            {
                if (_maskAdjustmentsDialog != null)
                    _maskAdjustmentsDialog.Close();
                CancelMaskLasso();
                ResetZoomPan();
            }

            if ((args.PropertyName is nameof(MainViewModel.IsMaskEditing)
                or nameof(MainViewModel.IsMaskAutoSelectMode)) && !_isPanning)
            {
                if (!ViewModel.IsMaskEditing)
                    CancelMaskLasso();
                PreviewArea.Cursor = (ViewModel.IsMaskEditing || ViewModel.IsMaskAutoSelectMode) && ViewModel.HasSelectedMaskLayer
                    ? Cursors.Cross
                    : null;
            }

            if (args.PropertyName is nameof(MainViewModel.SelectedMaskLayer)
                or nameof(MainViewModel.MaskCoverage)
                or nameof(MainViewModel.SourceFilePath))
            {
                if (args.PropertyName == nameof(MainViewModel.SelectedMaskLayer)
                    && _maskAdjustmentsDialog != null)
                {
                    ScheduleMaskAdjustmentsDialogSelectionValidation();
                }

                UpdateMaskOutlineOverlay();
            }
        };

        ViewModel.CompareModeChanged += OnCompareModeChanged;

        CropCanvas.SizeChanged += (_, _) => UpdateCropOverlay();
        GridOverlayCanvas.SizeChanged += (_, _) => UpdateGridOverlay();
        RulerHorizontal.SizeChanged += (_, _) => UpdateRulerOverlay();
        RulerVertical.SizeChanged += (_, _) => UpdateRulerOverlay();
    }

    // ============================================================
    //  メニュー・Undo/Redo
    // ============================================================

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_maskAdjustmentsDialog != null)
            _maskAdjustmentsDialog.Close();

        if (RightPanelColumn.Width.IsAbsolute)
        {
            var w = RightPanelColumn.Width.Value;
            if (w >= RightPanelMinWidth && w <= RightPanelMaxWidth)
                ThemeService.SaveRightPanelWidth(w);
        }
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            UpdateThemeMenuCheckmarks();
            // 書き出し設定を ViewModel に同期
            ViewModel.SelectedExportFormat = ThemeService.GetDefaultExportFormat();
            ViewModel.JpegQuality = ThemeService.GetDefaultJpegQuality();
            ViewModel.ShowRuleOfThirdsGrid = ThemeService.GetShowRuleOfThirdsGrid();
            ViewModel.ShowRuler = ThemeService.GetShowRuler();
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutDialog { Owner = this }.ShowDialog();
    }

    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = LocalizationService.GetString("Dialog.LogExport.Filter", "ZIP Files|*.zip"),
            FileName = $"VRCosme_Logs_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
            Title = LocalizationService.GetString("Dialog.LogExport.Title", "Export Logs")
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            LogService.Info($"ログ書き出し: {dialog.FileName}");
            LogService.ExportLog(dialog.FileName);
            MessageBox.Show(LocalizationService.GetString("Dialog.LogExport.Success", "Log export completed successfully."),
                LocalizationService.GetString("App.Name", "VRCosme"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                LocalizationService.Format("Dialog.LogExport.Failed", "Failed to export logs:\n{0}", ex.Message),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RightPanel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        if (HasAncestor<Slider>(source))
        {
            ViewModel.PushUndoSnapshot();
            return;
        }

        if (!ViewModel.HasSelectedMaskLayer)
            return;

        if (HasAncestor<ButtonBase>(source)
            || HasAncestor<TextBoxBase>(source)
            || HasAncestor<ComboBox>(source)
            || HasAncestor<ListBoxItem>(source)
            || HasAncestor<CheckBox>(source)
            || HasAncestor<RadioButton>(source)
            || HasAncestor<ScrollBar>(source)
            || HasAncestor<Thumb>(source)
            || HasAncestor<MenuItem>(source))
        {
            return;
        }

        if (source is Panel or Border or ScrollViewer)
            ViewModel.SelectedMaskLayer = null;
    }

    /// <summary>矢印キーでスライダー操作後もフォーカスが外れないようにする</summary>
    private void RightPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var focused = Keyboard.FocusedElement as DependencyObject;
        var slider = FindParent<Slider>(focused);
        if (slider == null) return;
        // 矢印キー処理後にフォーカスが他に移るのを防ぐため、処理後にスライダーへ再フォーカス
        Dispatcher.BeginInvoke(() => slider.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T t) return t;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private static bool HasAncestor<T>(DependencyObject? child) where T : DependencyObject =>
        FindParent<T>(child) != null;

    /// <summary>数値TextBoxでEnter押下時に適用し、同行情報のスライダーにフォーカスを移す</summary>
    private void ValueTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is not TextBox textBox) return;
        var grid = FindParent<Grid>(textBox);
        if (grid == null) return;
        foreach (var child in grid.Children.OfType<Slider>())
        {
            if (Grid.GetColumn(child) == 2)
            {
                child.Focus();
                e.Handled = true;
                break;
            }
        }
    }

    private void ThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string tag)
        {
            var mode = tag switch
            {
                "System" => AppThemeMode.System,
                "Light" => AppThemeMode.Light,
                "Dark" => AppThemeMode.Dark,
                _ => AppThemeMode.System
            };

            LogService.Info($"テーマ変更: {mode}");
            ThemeService.SaveThemeMode(mode);
            App.ApplyTheme(mode);
            UpdateThemeMenuCheckmarks();
        }
    }

    private void UpdateThemeMenuCheckmarks()
    {
        var currentMode = ThemeService.GetThemeMode();
        ThemeSystemMenuItem.IsChecked = currentMode == AppThemeMode.System;
        ThemeLightMenuItem.IsChecked = currentMode == AppThemeMode.Light;
        ThemeDarkMenuItem.IsChecked = currentMode == AppThemeMode.Dark;
    }

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

    /// <summary>source が parent のビジュアルツリー子孫かどうか判定</summary>
    private static bool IsDescendantOf(DependencyObject? source, DependencyObject parent)
    {
        while (source != null)
        {
            if (source == parent) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
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

    private void MaskLayerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MaskLayerList.SelectedItem == null) return;
        OpenMaskAdjustmentsPopup();
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

    private void UpdateAllOverlays()
    {
        UpdateCropOverlay();
        UpdateMaskOutlineOverlay();
        UpdateMaskLassoOverlay();
        UpdateGridOverlay();
        UpdateRulerOverlay();
        if (ViewModel.CompareMode == CompareMode.Split)
            UpdateSplitView();
    }

    // ── ツールバーボタン ──

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomByStep(true);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomByStep(false);

    // ── ツールバードラッグ ──

    private bool _isToolbarDragging;
    private Point _toolbarDragStart;

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

    // ============================================================
    //  レイアウト変更時の共通処理
    // ============================================================

    private void OnImageLayoutChanged()
    {
        UpdateCropOverlay();
        UpdateMaskOutlineOverlay();
        UpdateMaskLassoOverlay();
        UpdateGridOverlay();
        UpdateRulerOverlay();
        if (ViewModel.CompareMode == CompareMode.Split)
            UpdateSplitView();
    }

    // ============================================================
    //  ドラッグ & ドロップ
    // ============================================================

    private static readonly string[] SupportedExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".webp"];

    private void PreviewArea_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void PreviewArea_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void PreviewArea_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        if (files.Length == 0) return;
        var ext = System.IO.Path.GetExtension(files[0]).ToLowerInvariant();
        if (Array.Exists(SupportedExtensions, x => x == ext))
        {
            LogService.Info($"ドラッグ&ドロップ: {files[0]}");
            await ViewModel.LoadImageAsync(files[0]);
        }
    }

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

    private (Rect imageRect, double scale) GetImageRenderInfo()
    {
        if (PreviewImage.Source is not BitmapSource source)
            return (Rect.Empty, 1);

        double cw = PreviewImage.ActualWidth, ch = PreviewImage.ActualHeight;
        if (cw <= 0 || ch <= 0 || ViewModel.ImageWidth <= 0) return (Rect.Empty, 1);

        // Uniform stretch で画像がレイアウト内に収まるサイズ (ローカル座標)
        double imgA = (double)source.PixelWidth / source.PixelHeight;
        double ctlA = cw / ch;
        double rw, rh;
        if (imgA > ctlA) { rw = cw; rh = cw / imgA; }
        else { rh = ch; rw = ch * imgA; }

        // ローカル座標での画像原点 (中央揃え)
        double localX = (cw - rw) / 2;
        double localY = (ch - rh) / 2;

        // TranslatePoint でズーム・パンを含む画面座標に変換
        var topLeft = PreviewImage.TranslatePoint(new Point(localX, localY), CropCanvas);
        var bottomRight = PreviewImage.TranslatePoint(new Point(localX + rw, localY + rh), CropCanvas);

        double screenW = bottomRight.X - topLeft.X;
        double screenH = bottomRight.Y - topLeft.Y;
        if (screenW <= 0 || screenH <= 0) return (Rect.Empty, 1);

        return (new Rect(topLeft.X, topLeft.Y, screenW, screenH), screenW / ViewModel.ImageWidth);
    }

    private bool TryMapPreviewToImage(Point previewPos, out Point imagePos, out bool insideImage)
    {
        imagePos = new Point();
        insideImage = false;

        var (imageRect, scale) = GetImageRenderInfo();
        if (imageRect.IsEmpty || scale <= 0 || ViewModel.ImageWidth <= 0 || ViewModel.ImageHeight <= 0)
            return false;

        double rawX = (previewPos.X - imageRect.X) / scale;
        double rawY = (previewPos.Y - imageRect.Y) / scale;
        insideImage = rawX >= 0 && rawY >= 0
            && rawX <= ViewModel.ImageWidth - 1
            && rawY <= ViewModel.ImageHeight - 1;

        imagePos = new Point(
            Math.Clamp(rawX, 0, ViewModel.ImageWidth - 1),
            Math.Clamp(rawY, 0, ViewModel.ImageHeight - 1));
        return true;
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
            PreviewArea.Cursor = (ViewModel.IsMaskEditing || ViewModel.IsMaskAutoSelectMode) && ViewModel.HasSelectedMaskLayer
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
            PreviewArea.Cursor = (ViewModel.IsMaskEditing || ViewModel.IsMaskAutoSelectMode) && ViewModel.HasSelectedMaskLayer
                ? Cursors.Cross
                : null;
    }

    private void UpdateMaskOutlineOverlay()
    {
        if (_maskOutlinePath == null)
            return;

        if (!ViewModel.HasImage || !ViewModel.HasSelectedMaskLayer)
        {
            _maskOutlinePath.Data = null;
            _maskOutlinePath.Visibility = Visibility.Collapsed;
            return;
        }

        var state = ViewModel.GetSelectedMaskLayerState();
        if (state == null || state.NonZeroCount <= 0 || state.Width <= 0 || state.Height <= 0)
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
        var geometry = BuildMaskBoundaryGeometry(state, ir, scale);
        _maskOutlinePath.Data = geometry;
        _maskOutlinePath.Visibility = geometry == null ? Visibility.Collapsed : Visibility.Visible;
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

    private static StreamGeometry? BuildMaskBoundaryGeometry(MaskLayerState state, Rect imageRect, double scale)
    {
        if (state.MaskData.Length != state.Width * state.Height || state.NonZeroCount <= 0)
            return null;

        var edges = BuildBoundaryEdges(state.MaskData, state.Width, state.Height);
        if (edges.Count == 0)
            return null;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            DrawBoundaryContours(ctx, edges, imageRect, scale);
        }

        geometry.Freeze();
        return geometry;
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

    private static void DrawBoundaryContours(
        StreamGeometryContext ctx,
        IReadOnlyList<GridEdge> edges,
        Rect imageRect,
        double scale)
    {
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

            var first = ToPreviewPoint(simplified[0], imageRect, scale);
            ctx.BeginFigure(first, false, closed);
            for (int i = 1; i < simplified.Count; i++)
            {
                var p = ToPreviewPoint(simplified[i], imageRect, scale);
                ctx.LineTo(p, true, false);
            }
        }
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

    private static Point ToPreviewPoint((double X, double Y) point, Rect imageRect, double scale) =>
        new(imageRect.X + point.X * scale, imageRect.Y + point.Y * scale);

    private static Point ToPreviewPoint(GridPoint point, Rect imageRect, double scale) =>
        new(imageRect.X + point.X * scale, imageRect.Y + point.Y * scale);

    private readonly record struct GridPoint(int X, int Y);
    private readonly record struct GridEdge(GridPoint Start, GridPoint End);

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
