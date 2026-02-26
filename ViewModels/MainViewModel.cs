using CommunityToolkit.Mvvm.ComponentModel;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using VRCosme.Models;
using VRCosme.Services;
using VRCosme.Services.AI;

namespace VRCosme.ViewModels;

public enum CompareMode { After, Before, Split }

public partial class MainViewModel : ObservableObject
{
    // ───────── 画像状態 ─────────
    private Image<Rgba32>? _pristineImage;          // 読込時のオリジナル (変更しない)
    private Image<Rgba32>? _transformedImage;       // 回転・反転適用後
    private Image<Rgba32>? _previewSourceImage;     // プレビュー用ダウンサイズ
    private readonly object _imageSync = new();
    private bool _previewUpdatePending;
    private bool _previewUpdateRunning;
    private int _rotationDegrees;
    private bool _flipHorizontal;
    private bool _flipVertical;
    private const int MaxPreviewDimension = 1920;
    private readonly AutoMaskSelectorService _autoMaskSelector = new();

    // ───────── 表示プロパティ ─────────

    [ObservableProperty] private BitmapSource? _previewBitmap;
    [ObservableProperty] private BitmapSource? _beforeBitmap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    [NotifyPropertyChangedFor(nameof(CanRedo))]
    private string? _sourceFilePath;

    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _windowTitle = "";
    [ObservableProperty] private int _imageWidth;
    [ObservableProperty] private int _imageHeight;

    // ───────── 基本補正 ─────────

    [ObservableProperty] private double _brightness;      // -100 ～ 100
    [ObservableProperty] private double _contrast;        // -100 ～ 100
    [ObservableProperty] private double _gamma = 1.0;     // 0.2 ～ 5.0
    [ObservableProperty] private double _exposure;        // -5.0 ～ 5.0
    [ObservableProperty] private double _saturation;      // -100 ～ 100
    [ObservableProperty] private double _temperature;     // -100 ～ 100
    [ObservableProperty] private double _tint;            // -100 ～ 100

    // ───────── 詳細補正 ─────────

    [ObservableProperty] private double _shadows;         // -100 ～ 100
    [ObservableProperty] private double _highlights;      // -100 ～ 100
    [ObservableProperty] private double _clarity;         // -100 ～ 100
    [ObservableProperty] private double _blur;            // 0 ～ 100
    [ObservableProperty] private double _sharpen;         // 0 ～ 100
    [ObservableProperty] private double _vignette;        // -100 ～ 100

    // ───────── トリミング ─────────

    [ObservableProperty] private CropRatioItem _selectedCropRatio;
    [ObservableProperty] private bool _isCropActive;
    [ObservableProperty] private double _cropX;
    [ObservableProperty] private double _cropY;
    [ObservableProperty] private double _cropWidth;
    [ObservableProperty] private double _cropHeight;

    // ───────── 比較モード ─────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBeforeVisible))]
    [NotifyPropertyChangedFor(nameof(IsSplitMode))]
    private CompareMode _compareMode = CompareMode.After;

    [ObservableProperty] private double _splitPosition = 0.5;

    public bool IsBeforeVisible => CompareMode is CompareMode.Before or CompareMode.Split;
    public bool IsSplitMode => CompareMode == CompareMode.Split;

    // ───────── Undo / Redo ─────────

    /// <summary>Undo スタックの最大数。マスクデータ等でメモリを消費するため上限で抑える。</summary>
    private const int MaxUndoCount = 20;

    private readonly Stack<EditState> _undoStack = new();
    private readonly Stack<EditState> _redoStack = new();
    private bool _isRestoringState;
    private bool _isRelocalizing;

    public bool CanUndo => _undoStack.Count > 0 && HasImage;
    public bool CanRedo => _redoStack.Count > 0 && HasImage;

    // ───────── ズーム ─────────

    [ObservableProperty] private bool _isFitToScreen = true;

    // ───────── 表示オプション ─────────

    [ObservableProperty] private bool _showRuleOfThirdsGrid;
    [ObservableProperty] private bool _showRuler;

    // ───────── 書き出し ─────────

    [ObservableProperty] private string _selectedExportFormat = "PNG";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJpegSelected))]
    private int _jpegQuality = 90;

    // ───────── 算出プロパティ ─────────

    public bool HasImage => SourceFilePath != null;
    public bool IsJpegSelected => SelectedExportFormat == "JPEG";
    public double PreviewScale { get; private set; } = 1.0;

    // ───────── コレクション ─────────

    public List<CropRatioItem> CropRatios { get; private set; } = [];

    public List<string> ExportFormats { get; } = ["PNG", "JPEG"];
    public List<PresetItem> Presets { get; } = [];
    public ObservableCollection<string> RecentFiles { get; } = [];

    // ───────── イベント ─────────

    public event Action? CompareModeChanged;
    public event Action? ZoomModeChanged;

    // ───────── コンストラクタ ─────────

    public MainViewModel()
    {
        CropRatios = BuildCropRatios();
        _selectedCropRatio = CropRatios[0];
        _statusMessage = "";
        _windowTitle = LocalizationService.GetString("App.Name", "VRCosme");
        _showRuleOfThirdsGrid = ThemeService.GetShowRuleOfThirdsGrid();
        _showRuler = ThemeService.GetShowRuler();
        _selectedExportFormat = ThemeService.GetDefaultExportFormat();
        _jpegQuality = ThemeService.GetDefaultJpegQuality();
        LoadRecentFiles();
    }

    private void LoadRecentFiles()
    {
        RecentFiles.Clear();
        foreach (var f in RecentFilesService.Load())
            RecentFiles.Add(f);
    }

    public string BuildWindowTitle(string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return LocalizationService.GetString("App.Name", "VRCosme");

        return LocalizationService.Format("Window.TitleWithFile", "VRCosme - {0}",
            Path.GetFileName(filePath));
    }

    public string BuildReadyStatusMessage()
    {
        return "";
    }

    public void RefreshLocalization()
    {
        var selectedIndex = CropRatios.IndexOf(SelectedCropRatio);
        if (selectedIndex < 0) selectedIndex = 0;

        _isRelocalizing = true;
        try
        {
            CropRatios = BuildCropRatios();
            OnPropertyChanged(nameof(CropRatios));

            selectedIndex = Math.Clamp(selectedIndex, 0, CropRatios.Count - 1);
            SelectedCropRatio = CropRatios[selectedIndex];

            WindowTitle = BuildWindowTitle(SourceFilePath);
            if (!IsProcessing)
                StatusMessage = BuildReadyStatusMessage();
        }
        finally
        {
            _isRelocalizing = false;
        }
    }

    private static List<CropRatioItem> BuildCropRatios() =>
    [
        new(LocalizationService.GetString("Crop.None", "None"), 0, 0),
        new(LocalizationService.GetString("Crop.Square", "1:1 (Square)"), 1, 1),
        new(LocalizationService.GetString("Crop.Wide16by9", "16:9 (Landscape)"), 16, 9),
        new(LocalizationService.GetString("Crop.Tall9by16", "9:16 (Portrait)"), 9, 16),
        new(LocalizationService.GetString("Crop.Wide4by3", "4:3 (Landscape)"), 4, 3),
        new(LocalizationService.GetString("Crop.Tall3by4X", "3:4 (Portrait, good for X posts)"), 3, 4),
        new(LocalizationService.GetString("Crop.Header3by1", "3:1 (Header)"), 3, 1),
    ];

    // ───────── 補正値ヘルパー ─────────

    internal AdjustmentValues BuildAdjustmentValues() => new(
        Brightness, Contrast, Gamma, Exposure, Saturation, Temperature, Tint,
        Shadows, Highlights, Clarity, Blur, Sharpen, Vignette);

    internal void RestoreAdjustmentValues(AdjustmentValues v)
    {
        Brightness = v.Brightness;
        Contrast = v.Contrast;
        Gamma = v.Gamma;
        Exposure = v.Exposure;
        Saturation = v.Saturation;
        Temperature = v.Temperature;
        Tint = v.Tint;
        Shadows = v.Shadows;
        Highlights = v.Highlights;
        Clarity = v.Clarity;
        Blur = v.Blur;
        Sharpen = v.Sharpen;
        Vignette = v.Vignette;
    }
}
