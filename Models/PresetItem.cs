namespace VRCosme.Models;

/// <summary>画像補正プリセット</summary>
public class PresetItem
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    // 基本補正 (各スライダーと同じ範囲)
    public double Brightness { get; init; }     // -100 ～ 100
    public double Contrast { get; init; }       // -100 ～ 100
    public double Gamma { get; init; } = 1.0;   // 0.2 ～ 5.0
    public double Exposure { get; init; }       // -5.0 ～ 5.0
    public double Saturation { get; init; }     // -100 ～ 100
    public double Temperature { get; init; }    // -100 ～ 100
    public double Tint { get; init; }           // -100 ～ 100

    // 詳細補正
    public double Shadows { get; init; }        // -100 ～ 100
    public double Highlights { get; init; }     // -100 ～ 100
    public double Clarity { get; init; }        // -100 ～ 100
    public double Blur { get; init; }           // 0 ～ 100
    public double Sharpen { get; init; }        // 0 ～ 100
    public double Vignette { get; init; }       // -100 ～ 100

    public override string ToString() => Name;
}
