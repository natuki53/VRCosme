using CommunityToolkit.Mvvm.ComponentModel;

namespace VRCosme.Models;

public partial class MaskLayer : ObservableObject
{
    [ObservableProperty]
    private string _name;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int NonZeroCount { get; private set; }

    public AdjustmentValues Adjustments { get; set; } = AdjustmentValues.Default;
    public bool NaturalizeBoundary { get; set; }

    public byte[] MaskData { get; private set; }

    public MaskLayer(string name, int width, int height)
    {
        _name = name;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        MaskData = new byte[Width * Height];
    }

    public bool HasMask => NonZeroCount > 0;

    public double CoveragePercent => Width > 0 && Height > 0
        ? NonZeroCount * 100.0 / (Width * Height)
        : 0;

    public void ClearMask()
    {
        Array.Clear(MaskData);
        NonZeroCount = 0;
    }

    public void SetMaskData(byte[] data, int width, int height, int nonZeroCount)
    {
        if (width <= 0 || height <= 0 || data.Length != width * height)
            throw new ArgumentException("Invalid mask data shape.", nameof(data));

        Width = width;
        Height = height;
        MaskData = (byte[])data.Clone();
        NonZeroCount = Math.Clamp(nonZeroCount, 0, MaskData.Length);
        if (NonZeroCount == 0)
        {
            for (int i = 0; i < MaskData.Length; i++)
                if (MaskData[i] != 0) NonZeroCount++;
        }
    }

    public void SetMaskPixel(int index, byte value)
    {
        var old = MaskData[index];
        if (old == value) return;
        MaskData[index] = value;
        if (old == 0 && value > 0) NonZeroCount++;
        else if (old > 0 && value == 0) NonZeroCount--;
    }

    public void ResetMaskSize(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        MaskData = new byte[Width * Height];
        NonZeroCount = 0;
    }

    public MaskLayerState CreateState() =>
        new(
            Name,
            (byte[])MaskData.Clone(),
            Width,
            Height,
            NonZeroCount,
            Adjustments,
            NaturalizeBoundary
        );

    public static MaskLayer FromState(MaskLayerState state)
    {
        var layer = new MaskLayer(state.Name, state.Width, state.Height)
        {
            Adjustments = state.Adjustments,
            NaturalizeBoundary = state.NaturalizeBoundary
        };
        layer.SetMaskData(state.MaskData, state.Width, state.Height, state.NonZeroCount);
        return layer;
    }
}
