using CommunityToolkit.Mvvm.ComponentModel;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    private readonly SamSelectorService _samSelector = new();
    private int _imageVersion;

    // ───────── 表示プロパティ ─────────

    [ObservableProperty] private BitmapSource? _previewBitmap;
    [ObservableProperty] private BitmapSource? _beforeBitmap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    [NotifyPropertyChangedFor(nameof(CanRedo))]
    [NotifyCanExecuteChangedFor(nameof(SaveSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSessionAsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCropCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyPresetCommand))]
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
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCropCommand))]
    private bool _isCropActive;
    [ObservableProperty] private double _cropX;
    [ObservableProperty] private double _cropY;
    [ObservableProperty] private double _cropWidth;
    [ObservableProperty] private double _cropHeight;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCropCommand))]
    private bool _isCropApplied;

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
    private AdjustmentValues? _presetBaseAdjustments;
    private AdjustmentValues? _lastAppliedPresetAdjustments;
    private string? _presetBaseKey;

    private void ClearPresetApplicationContext()
    {
        _presetBaseAdjustments = null;
        _lastAppliedPresetAdjustments = null;
        _presetBaseKey = null;
    }

    public bool CanUndo => _undoStack.Count > 0 && HasImage;
    public bool CanRedo => _redoStack.Count > 0 && HasImage;

    // ───────── ズーム ─────────

    [ObservableProperty] private bool _isFitToScreen = true;

    // ───────── 表示オプション ─────────

    [ObservableProperty] private bool _showRuleOfThirdsGrid;
    [ObservableProperty] private bool _showRuler;

    // ───────── セッション保存 ─────────

    [ObservableProperty] private string? _currentSessionPath;
    [ObservableProperty] private bool _isDirty;

    // ───────── 書き出し ─────────

    [ObservableProperty] private string _selectedExportFormat = "PNG";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJpegSelected))]
    private int _jpegQuality = 90;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyPresetCommand))]
    private PresetItem? _selectedPreset;
    [ObservableProperty] private double _presetStrength = 50.0;

    // ───────── 算出プロパティ ─────────

    public bool HasImage => SourceFilePath != null;
    public bool IsJpegSelected => SelectedExportFormat == "JPEG";
    public double PreviewScale { get; private set; } = 1.0;

    // ───────── コレクション ─────────

    public List<CropRatioItem> CropRatios { get; private set; } = [];

    public List<string> ExportFormats { get; } = ["PNG", "JPEG"];
    public List<PresetItem> Presets { get; private set; } = [];
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
        _windowTitle = LocalizationService.GetString("App.Name", "VRCosme Classic");
        _showRuleOfThirdsGrid = ThemeService.GetShowRuleOfThirdsGrid();
        _showRuler = ThemeService.GetShowRuler();
        _selectedExportFormat = ThemeService.GetDefaultExportFormat();
        _jpegQuality = ThemeService.GetDefaultJpegQuality();
        Presets = BuildPresets();
        _selectedPreset = Presets.FirstOrDefault();
        SyncPresetSelectionFlags();
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
        string title;
        if (string.IsNullOrWhiteSpace(filePath))
            title = LocalizationService.GetString("App.Name", "VRCosme Classic");
        else
            title = LocalizationService.Format("Window.TitleWithFile", "VRCosme Classic - {0}",
                Path.GetFileName(filePath));

        return IsDirty ? $"{title} *" : title;
    }

    public string BuildReadyStatusMessage()
    {
        return "";
    }

    public void RefreshLocalization()
    {
        var selectedIndex = CropRatios.IndexOf(SelectedCropRatio);
        if (selectedIndex < 0) selectedIndex = 0;
        var selectedPresetIndex = SelectedPreset is null ? 0 : Presets.IndexOf(SelectedPreset);
        if (selectedPresetIndex < 0) selectedPresetIndex = 0;

        _isRelocalizing = true;
        try
        {
            CropRatios = BuildCropRatios();
            OnPropertyChanged(nameof(CropRatios));

            selectedIndex = Math.Clamp(selectedIndex, 0, CropRatios.Count - 1);
            SelectedCropRatio = CropRatios[selectedIndex];

            Presets = BuildPresets();
            OnPropertyChanged(nameof(Presets));
            if (Presets.Count == 0)
                SelectedPreset = null;
            else
            {
                selectedPresetIndex = Math.Clamp(selectedPresetIndex, 0, Presets.Count - 1);
                SelectedPreset = Presets[selectedPresetIndex];
            }

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
        new(LocalizationService.GetString("Crop.Free", "Free"), -1, -1),
    ];

    private static List<PresetItem> BuildPresets() =>
    [
        new()
        {
            Name = LocalizationService.GetString("Preset.SoftPortrait.Name", "Soft Portrait"),
            Description = LocalizationService.GetString("Preset.SoftPortrait.Description", "Bright and warm portrait look with gentle contrast."),
            Adjustments = new AdjustmentValues(6, 8, 1.05, 0.25, 6, 8, 2, 18, -12, 4, 0, 8, -6)
        },
        new()
        {
            Name = LocalizationService.GetString("Preset.NeonNight.Name", "Neon Night"),
            Description = LocalizationService.GetString("Preset.NeonNight.Description", "Cool neon atmosphere with stronger contrast and saturation."),
            Adjustments = new AdjustmentValues(-6, 20, 0.98, 0.1, 24, -12, 10, -14, -20, 18, 0, 14, -22)
        },
        new()
        {
            Name = LocalizationService.GetString("Preset.SunsetFilm.Name", "Sunset Film"),
            Description = LocalizationService.GetString("Preset.SunsetFilm.Description", "Warm cinematic tone with soft highlights."),
            Adjustments = new AdjustmentValues(4, 10, 1.02, 0.2, 12, 18, 4, 10, -14, 6, 0, 10, -14)
        },
        new()
        {
            Name = LocalizationService.GetString("Preset.CoolClear.Name", "Cool Clear"),
            Description = LocalizationService.GetString("Preset.CoolClear.Description", "Crisp and cool finish that keeps details sharp."),
            Adjustments = new AdjustmentValues(2, 14, 1.0, 0.05, 8, -10, -4, 4, -8, 16, 0, 18, -8)
        },
        new()
        {
            Name = LocalizationService.GetString("Preset.MatteSoft.Name", "Matte Soft"),
            Description = LocalizationService.GetString("Preset.MatteSoft.Description", "Soft matte vibe with raised shadows and low contrast."),
            Adjustments = new AdjustmentValues(8, -8, 1.08, 0.35, -6, 6, 0, 22, -4, -8, 2, 4, -4)
        },
        new()
        {
            Name = LocalizationService.GetString("Preset.PunchyDetail.Name", "Punchy Detail"),
            Description = LocalizationService.GetString("Preset.PunchyDetail.Description", "High-impact detail and contrast for dramatic shots."),
            Adjustments = new AdjustmentValues(-2, 24, 0.96, 0.0, 4, 0, 0, -20, -10, 22, 0, 20, -18)
        },
    ];

    private void SyncPresetSelectionFlags()
    {
        foreach (var preset in Presets)
            preset.IsSelected = ReferenceEquals(preset, SelectedPreset);
    }

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
