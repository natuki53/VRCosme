using CommunityToolkit.Mvvm.Input;
using VRCosme.Models;
using VRCosme.Services;

namespace VRCosme.ViewModels;

public partial class MainViewModel
{
    private void PushUndoState(EditState snapshot)
    {
        if (_isRestoringState || !HasImage) return;
        _undoStack.Push(snapshot);
        if (_undoStack.Count > MaxUndoCount)
        {
            var list = _undoStack.ToList();
            _undoStack.Clear();
            for (int i = MaxUndoCount - 1; i >= 0; i--)
                _undoStack.Push(list[i]);
        }

        _redoStack.Clear();
        NotifyUndoRedoChanged();
    }

    private void NotifyUndoRedoChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void ReplaceLatestUndoState(EditState snapshot)
    {
        if (_undoStack.Count == 0)
            return;

        _undoStack.Pop();
        _undoStack.Push(snapshot);
        NotifyUndoRedoChanged();
    }

    private EditState CreateSnapshot()
    {
        var cropIndex = CropRatios.IndexOf(SelectedCropRatio);
        if (cropIndex < 0) cropIndex = 0;
        var mask = CloneMaskSnapshot();
        return new EditState(
            BuildAdjustmentValues(),
            IsCropActive, CropX, CropY, CropWidth, CropHeight, cropIndex,
            _rotationDegrees, _flipHorizontal, _flipVertical,
            IsMaskEnabled, mask.SelectedIndex, mask.Layers);
    }

    /// <summary>編集開始前に View から呼ぶ。現在状態を Undo スタックに積む。</summary>
    public void PushUndoSnapshot()
    {
        PushUndoState(CreateSnapshot());
    }

    private async Task RestoreStateAsync(EditState state)
    {
        _isRestoringState = true;
        try
        {
            RestoreAdjustmentValues(state.Adjustments);

            _rotationDegrees = state.RotationDegrees;
            _flipHorizontal = state.FlipHorizontal;
            _flipVertical = state.FlipVertical;
            IsMaskEnabled = state.IsMaskEnabled;
            if (_pristineImage != null)
                await ApplyTransformAsync();

            RestoreMaskSnapshot(state.MaskLayers, state.SelectedMaskLayerIndex);
            SchedulePreviewUpdate();

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
