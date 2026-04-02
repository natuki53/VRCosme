using CommunityToolkit.Mvvm.ComponentModel;

namespace VRCosme.Models;

/// <summary>画像補正プリセット</summary>
public partial class PresetItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    public AdjustmentValues Adjustments { get; init; } = AdjustmentValues.Default;
    [ObservableProperty] private bool _isSelected;

    public override string ToString() => Name;
}
