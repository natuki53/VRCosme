using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VRCosme.Models;
using VRCosme.Services;
using VRCosme.ViewModels;
using VRCosme.Views;

namespace VRCosme;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

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
                or nameof(MainViewModel.IsMaskEraseMode)
                or nameof(MainViewModel.IsMaskColorAutoSelectMode)
                or nameof(MainViewModel.IsMaskAutoSelectMode)) && !_isPanning)
            {
                if (!ViewModel.IsMaskEditing && !ViewModel.IsMaskEraseMode)
                    CancelMaskLasso();
                PreviewArea.Cursor = (ViewModel.IsMaskEditing || ViewModel.IsMaskEraseMode || ViewModel.IsMaskColorAutoSelectMode || ViewModel.IsMaskAutoSelectMode)
                    && ViewModel.HasSelectedMaskLayer
                    ? Cursors.Cross
                    : null;
            }

            if (args.PropertyName is nameof(MainViewModel.SelectedMaskLayer)
                or nameof(MainViewModel.MaskRevision)
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
    //  メニュー・設定
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
        var dialog = new Microsoft.Win32.SaveFileDialog
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

    // ============================================================
    //  右パネル入力
    // ============================================================

    private void RightPanel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        var slider = FindParent<Slider>(source);
        if (slider != null)
        {
            if (!string.Equals(slider.Tag as string, "NoUndo", StringComparison.Ordinal))
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

    // ============================================================
    //  テーマ
    // ============================================================

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
    //  共有ユーティリティ
    // ============================================================

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
}
