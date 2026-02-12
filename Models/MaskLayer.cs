using CommunityToolkit.Mvvm.ComponentModel;

namespace VRCosme.Models;

public partial class MaskLayer : ObservableObject
{
    [ObservableProperty]
    private string _name;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int NonZeroCount { get; private set; }

    public double Brightness { get; set; }
    public double Contrast { get; set; }
    public double Gamma { get; set; } = 1.0;
    public double Exposure { get; set; }
    public double Saturation { get; set; }
    public double Temperature { get; set; }
    public double Tint { get; set; }
    public double Shadows { get; set; }
    public double Highlights { get; set; }
    public double Clarity { get; set; }
    public double Blur { get; set; }
    public double Sharpen { get; set; }
    public double Vignette { get; set; }
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
            Brightness,
            Contrast,
            Gamma,
            Exposure,
            Saturation,
            Temperature,
            Tint,
            Shadows,
            Highlights,
            Clarity,
            Blur,
            Sharpen,
            Vignette,
            NaturalizeBoundary
        );

    public static MaskLayer FromState(MaskLayerState state)
    {
        var layer = new MaskLayer(state.Name, state.Width, state.Height)
        {
            Brightness = state.Brightness,
            Contrast = state.Contrast,
            Gamma = state.Gamma,
            Exposure = state.Exposure,
            Saturation = state.Saturation,
            Temperature = state.Temperature,
            Tint = state.Tint,
            Shadows = state.Shadows,
            Highlights = state.Highlights,
            Clarity = state.Clarity,
            Blur = state.Blur,
            Sharpen = state.Sharpen,
            Vignette = state.Vignette,
            NaturalizeBoundary = state.NaturalizeBoundary
        };
        layer.SetMaskData(state.MaskData, state.Width, state.Height, state.NonZeroCount);
        return layer;
    }
}
