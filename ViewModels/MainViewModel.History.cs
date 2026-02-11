using CommunityToolkit.Mvvm.Input;
using VRCosme.Models;
using VRCosme.Services;

namespace VRCosme.ViewModels;

public partial class MainViewModel
{
    private void NotifyUndoRedoChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private EditState CreateSnapshot()
    {
        var cropIndex = CropRatios.IndexOf(SelectedCropRatio);
        if (cropIndex < 0) cropIndex = 0;
        return new EditState(
            Brightness, Contrast, Gamma, Exposure, Saturation, Temperature, Tint,
            Shadows, Highlights, Clarity, Sharpen, Vignette,
            IsCropActive, CropX, CropY, CropWidth, CropHeight, cropIndex,
            _rotationDegrees, _flipHorizontal, _flipVertical);
    }

    /// <summary>編集開始前に View から呼ぶ。現在状態を Undo スタックに積む。</summary>
    public void PushUndoSnapshot()
    {
        if (_isRestoringState || !HasImage) return;
        _undoStack.Push(CreateSnapshot());
        _redoStack.Clear();
        NotifyUndoRedoChanged();
    }

    private async Task RestoreStateAsync(EditState state)
    {
        _isRestoringState = true;
        try
        {
            Brightness = state.Brightness;
            Contrast = state.Contrast;
            Gamma = state.Gamma;
            Exposure = state.Exposure;
            Saturation = state.Saturation;
            Temperature = state.Temperature;
            Tint = state.Tint;
            Shadows = state.Shadows;
            Highlights = state.Highlights;
            Clarity = state.Clarity;
            Sharpen = state.Sharpen;
            Vignette = state.Vignette;

            _rotationDegrees = state.RotationDegrees;
            _flipHorizontal = state.FlipHorizontal;
            _flipVertical = state.FlipVertical;
            if (_pristineImage != null)
                await ApplyTransformAsync();

            var cropIndex = Math.Clamp(state.SelectedCropRatioIndex, 0, CropRatios.Count - 1);
            SelectedCropRatio = CropRatios[cropIndex];
            IsCropActive = state.IsCropActive;
            CropX = state.CropX;
            CropY = state.CropY;
            CropWidth = state.CropWidth;
            CropHeight = state.CropHeight;
        }
        finally
        {
            _isRestoringState = false;
            NotifyUndoRedoChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (_undoStack.Count == 0 || !HasImage) return;
        LogService.Info($"Undo (残り={_undoStack.Count - 1})");
        var stateBefore = CreateSnapshot();
        var toRestore = _undoStack.Pop();
        _redoStack.Push(stateBefore);
        await RestoreStateAsync(toRestore);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task RedoAsync()
    {
        if (_redoStack.Count == 0 || !HasImage) return;
        LogService.Info($"Redo (残り={_redoStack.Count - 1})");
        var stateBefore = CreateSnapshot();
        var toRestore = _redoStack.Pop();
        _undoStack.Push(stateBefore);
        await RestoreStateAsync(toRestore);
    }
}
