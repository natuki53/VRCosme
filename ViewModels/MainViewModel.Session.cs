using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using VRCosme.Models;
using VRCosme.Services;
using VRCosme.Views;

namespace VRCosme.ViewModels;

public partial class MainViewModel
{
    private const string SessionFileExtension = "vrcproj";
    private const double StateCompareEpsilon = 0.0001;
    private EditState? _cleanEditState;

    [RelayCommand]
    private async Task OpenSessionAsync()
    {
        if (!ConfirmDiscardChangesOrSaveIfNeeded())
            return;

        var dialog = new OpenFileDialog
        {
            Filter = LocalizationService.GetString(
                "Dialog.Session.Filter",
                "VRCosme Classic Session|*.vrcproj|All Files|*.*"),
            DefaultExt = SessionFileExtension,
            Title = LocalizationService.GetString("Dialog.Session.OpenTitle", "Open Session")
        };

        string? initialDirectory = null;
        if (!string.IsNullOrWhiteSpace(CurrentSessionPath))
        {
            var currentDir = Path.GetDirectoryName(Path.GetFullPath(CurrentSessionPath));
            if (!string.IsNullOrWhiteSpace(currentDir) && Directory.Exists(currentDir))
                initialDirectory = currentDir;
        }

        if (string.IsNullOrWhiteSpace(initialDirectory))
            initialDirectory = EnsureDefaultSessionDirectoryOrNull();

        if (!string.IsNullOrWhiteSpace(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        if (dialog.ShowDialog() != true)
            return;

        await LoadSessionFromFileAsync(dialog.FileName);
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void SaveSession() =>
        SaveSessionCore(saveAs: false, showSuccessMessage: false);

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void SaveSessionAs() =>
        SaveSessionCore(saveAs: true, showSuccessMessage: false);

    public bool ConfirmDiscardChangesOrSaveIfNeeded()
    {
        if (!HasImage)
            return true;

        ReconcileDirtyStateIfNeeded();
        if (!IsDirty)
            return true;

        var dialog = new UnsavedChangesDialog();
        if (Application.Current?.MainWindow != null)
            dialog.Owner = Application.Current.MainWindow;

        dialog.ShowDialog();

        return dialog.Result switch
        {
            UnsavedChangesDialogResult.Save => SaveSessionCore(saveAs: false, showSuccessMessage: false),
            UnsavedChangesDialogResult.Discard => true,
            _ => false
        };
    }

    private bool SaveSessionCore(bool saveAs, bool showSuccessMessage)
    {
        if (!HasImage || string.IsNullOrWhiteSpace(SourceFilePath))
            return false;

        var sessionPath = CurrentSessionPath;
        if (saveAs || string.IsNullOrWhiteSpace(sessionPath))
            sessionPath = PromptSessionSavePath(CurrentSessionPath);

        if (string.IsNullOrWhiteSpace(sessionPath))
            return false;

        try
        {
            var snapshot = CreateSnapshot();
            var document = BuildSessionDocument(SourceFilePath, snapshot);
            SessionService.Save(sessionPath, document);

            var fullSessionPath = Path.GetFullPath(sessionPath);
            MarkCurrentSessionClean(fullSessionPath);
            StatusMessage = LocalizationService.GetString("Status.SessionSaved", "Session saved");
            LogService.Info($"セッション保存完了: {fullSessionPath}");

            if (showSuccessMessage)
            {
                MessageBox.Show(
                    LocalizationService.GetString("Dialog.Session.SaveSuccess", "Session saved successfully."),
                    LocalizationService.GetString("App.Name", "VRCosme Classic"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("セッション保存に失敗", ex);
            MessageBox.Show(
                LocalizationService.Format(
                    "Dialog.Session.SaveFailed",
                    "Failed to save session:\n{0}",
                    ex.Message),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private async Task LoadSessionFromFileAsync(string sessionPath)
    {
        try
        {
            var fullSessionPath = Path.GetFullPath(sessionPath);
            var document = SessionService.Load(fullSessionPath);

            var sourceImagePath = SessionService.ResolveSourceImagePath(fullSessionPath, document);
            if (string.IsNullOrWhiteSpace(sourceImagePath))
            {
                MessageBox.Show(
                    LocalizationService.Format(
                        "Dialog.Session.ImageMissing",
                        "The source image was not found:\n{0}\n\nPlease select the source image file.",
                        document.SourceFilePath),
                    LocalizationService.GetString("Dialog.Session.ImageMissingTitle", "Source Image Not Found"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                sourceImagePath = PromptSessionSourceImagePath(document.SourceFilePath);
                if (string.IsNullOrWhiteSpace(sourceImagePath))
                    return;
            }

            await LoadImageAsync(sourceImagePath, confirmDiscardUnsavedChanges: false);
            if (_transformedImage is null || !HasImage)
                return;

            var state = BuildEditState(document);
            await RestoreStateAsync(state);

            _undoStack.Clear();
            _redoStack.Clear();
            NotifyUndoRedoChanged();

            MarkCurrentSessionClean(fullSessionPath);
            StatusMessage = LocalizationService.GetString("Status.SessionLoaded", "Session loaded");
            LogService.Info($"セッション読み込み完了: {fullSessionPath}");
        }
        catch (Exception ex)
        {
            LogService.Error("セッション読み込みに失敗", ex);
            MessageBox.Show(
                LocalizationService.Format(
                    "Dialog.Session.LoadFailed",
                    "Failed to load session:\n{0}",
                    ex.Message),
                LocalizationService.GetString("Dialog.ErrorTitle", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private string? PromptSessionSavePath(string? currentSessionPath)
    {
        var dialog = new SaveFileDialog
        {
            Filter = LocalizationService.GetString(
                "Dialog.Session.Filter",
                "VRCosme Classic Session|*.vrcproj|All Files|*.*"),
            DefaultExt = SessionFileExtension,
            AddExtension = true,
            Title = LocalizationService.GetString("Dialog.Session.SaveTitle", "Save Session")
        };

        if (!string.IsNullOrWhiteSpace(currentSessionPath))
        {
            var currentFullPath = Path.GetFullPath(currentSessionPath);
            var dir = Path.GetDirectoryName(currentFullPath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                dialog.InitialDirectory = dir;
            dialog.FileName = Path.GetFileName(currentFullPath);
        }
        else
        {
            var defaultSessionDir = EnsureDefaultSessionDirectoryOrNull();
            if (!string.IsNullOrWhiteSpace(defaultSessionDir))
                dialog.InitialDirectory = defaultSessionDir;

            if (!string.IsNullOrWhiteSpace(SourceFilePath))
            {
                var sourceFullPath = Path.GetFullPath(SourceFilePath);
                dialog.FileName = Path.GetFileNameWithoutExtension(sourceFullPath) + ".vrcproj";
            }
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private string? PromptSessionSourceImagePath(string? sourceFilePath)
    {
        var dialog = new OpenFileDialog
        {
            Filter = LocalizationService.GetString(
                "Dialog.OpenImage.Filter",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif;*.webp|All Files|*.*"),
            Title = LocalizationService.GetString(
                "Dialog.Session.SelectSourceImageTitle",
                "Select Source Image")
        };

        if (!string.IsNullOrWhiteSpace(sourceFilePath))
        {
            try
            {
                var fullPath = Path.GetFullPath(sourceFilePath);
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    dialog.InitialDirectory = dir;
                dialog.FileName = Path.GetFileName(fullPath);
            }
            catch
            {
                // ignore path parse errors
            }
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static SessionDocument BuildSessionDocument(string sourceFilePath, EditState snapshot)
    {
        return new SessionDocument
        {
            SourceFilePath = sourceFilePath,
            Adjustments = snapshot.Adjustments,
            IsCropActive = snapshot.IsCropActive,
            CropX = snapshot.CropX,
            CropY = snapshot.CropY,
            CropWidth = snapshot.CropWidth,
            CropHeight = snapshot.CropHeight,
            IsCropApplied = snapshot.IsCropApplied,
            SelectedCropRatioIndex = snapshot.SelectedCropRatioIndex,
            RotationDegrees = snapshot.RotationDegrees,
            FlipHorizontal = snapshot.FlipHorizontal,
            FlipVertical = snapshot.FlipVertical,
            IsMaskEnabled = snapshot.IsMaskEnabled,
            SelectedMaskLayerIndex = snapshot.SelectedMaskLayerIndex,
            MaskLayers = snapshot.MaskLayers.Select(CloneMaskLayerState).ToList()
        };
    }

    private static EditState BuildEditState(SessionDocument document)
    {
        var layers = document.MaskLayers.Select(CloneMaskLayerState).ToList();
        var isCropApplied = document.Version <= 1
            ? document.IsCropActive
            : document.IsCropApplied;
        return new EditState(
            document.Adjustments,
            document.IsCropActive,
            document.CropX,
            document.CropY,
            document.CropWidth,
            document.CropHeight,
            isCropApplied,
            document.SelectedCropRatioIndex,
            document.RotationDegrees,
            document.FlipHorizontal,
            document.FlipVertical,
            document.IsMaskEnabled,
            document.SelectedMaskLayerIndex,
            layers);
    }

    private static MaskLayerState CloneMaskLayerState(MaskLayerState state) =>
        new(
            state.Name,
            (byte[])state.MaskData.Clone(),
            state.Width,
            state.Height,
            state.NonZeroCount,
            state.Adjustments,
            state.NaturalizeBoundary);

    private void MarkCurrentSessionClean(string? sessionPath)
    {
        CurrentSessionPath = sessionPath;
        _cleanEditState = HasImage ? CreateSnapshot() : null;
        IsDirty = false;
    }

    private void MarkCurrentSessionDirty()
    {
        if (!HasImage || _isRestoringState || IsProcessing)
            return;

        IsDirty = true;
    }

    private static string? EnsureDefaultSessionDirectoryOrNull()
    {
        try
        {
            return SessionService.EnsureDefaultSessionDirectory();
        }
        catch
        {
            return null;
        }
    }

    private void ReconcileDirtyStateIfNeeded()
    {
        if (!IsDirty || !HasImage || _cleanEditState is null || _isRestoringState)
            return;

        var current = CreateSnapshot();
        if (AreEditStatesEquivalent(current, _cleanEditState))
            IsDirty = false;
    }

    private static bool AreEditStatesEquivalent(EditState a, EditState b)
    {
        if (!AreAdjustmentValuesEquivalent(a.Adjustments, b.Adjustments))
            return false;

        bool aCropActive = a.IsCropActive;
        bool bCropActive = b.IsCropActive;
        bool aCropApplied = a.IsCropActive && a.IsCropApplied;
        bool bCropApplied = b.IsCropActive && b.IsCropApplied;

        if (aCropActive != bCropActive
            || aCropApplied != bCropApplied
            || a.RotationDegrees != b.RotationDegrees
            || a.FlipHorizontal != b.FlipHorizontal
            || a.FlipVertical != b.FlipVertical
            || a.IsMaskEnabled != b.IsMaskEnabled
            || a.SelectedMaskLayerIndex != b.SelectedMaskLayerIndex)
        {
            return false;
        }

        // Crop が無効のときは座標/比率の保持値差分は未保存判定に使わない。
        if (aCropActive
            && (a.SelectedCropRatioIndex != b.SelectedCropRatioIndex
                || !AreClose(a.CropX, b.CropX)
                || !AreClose(a.CropY, b.CropY)
                || !AreClose(a.CropWidth, b.CropWidth)
                || !AreClose(a.CropHeight, b.CropHeight)))
        {
            return false;
        }

        if (a.MaskLayers.Count != b.MaskLayers.Count)
            return false;

        for (int i = 0; i < a.MaskLayers.Count; i++)
        {
            if (!AreMaskLayerStatesEquivalent(a.MaskLayers[i], b.MaskLayers[i]))
                return false;
        }

        return true;
    }

    private static bool AreMaskLayerStatesEquivalent(MaskLayerState a, MaskLayerState b)
    {
        if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            || a.Width != b.Width
            || a.Height != b.Height
            || a.NonZeroCount != b.NonZeroCount
            || !AreAdjustmentValuesEquivalent(a.Adjustments, b.Adjustments)
            || a.NaturalizeBoundary != b.NaturalizeBoundary
            || a.MaskData.Length != b.MaskData.Length)
        {
            return false;
        }

        for (int i = 0; i < a.MaskData.Length; i++)
        {
            if (a.MaskData[i] != b.MaskData[i])
                return false;
        }

        return true;
    }

    private static bool AreAdjustmentValuesEquivalent(AdjustmentValues a, AdjustmentValues b)
    {
        return AreClose(a.Brightness, b.Brightness)
            && AreClose(a.Contrast, b.Contrast)
            && AreClose(a.Gamma, b.Gamma)
            && AreClose(a.Exposure, b.Exposure)
            && AreClose(a.Saturation, b.Saturation)
            && AreClose(a.Temperature, b.Temperature)
            && AreClose(a.Tint, b.Tint)
            && AreClose(a.Shadows, b.Shadows)
            && AreClose(a.Highlights, b.Highlights)
            && AreClose(a.Clarity, b.Clarity)
            && AreClose(a.Blur, b.Blur)
            && AreClose(a.Sharpen, b.Sharpen)
            && AreClose(a.Vignette, b.Vignette);
    }

    private static bool AreClose(double a, double b) =>
        Math.Abs(a - b) <= StateCompareEpsilon;
}
