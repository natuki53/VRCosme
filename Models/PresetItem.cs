namespace VRCosme.Models;

/// <summary>画像補正プリセット</summary>
public class PresetItem
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    public AdjustmentValues Adjustments { get; init; } = AdjustmentValues.Default;

    public override string ToString() => Name;
}
