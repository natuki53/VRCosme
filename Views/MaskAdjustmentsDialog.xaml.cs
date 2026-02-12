using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Linq;
using VRCosme.Models;

namespace VRCosme.Views;

public partial class MaskAdjustmentsDialog : Window
{
    private bool _isInitializing;

    public bool IsConfirmed { get; private set; }
    public MaskAdjustmentValues Values { get; private set; }
    public event Action<MaskAdjustmentValues>? ValuesChanged;

    public MaskAdjustmentsDialog(string layerName, MaskAdjustmentValues values)
    {
        InitializeComponent();
        LayerNameText.Text = layerName;
        _isInitializing = true;

        BrightnessSlider.Value = values.Brightness;
        ContrastSlider.Value = values.Contrast;
        GammaSlider.Value = values.Gamma;
        ExposureSlider.Value = values.Exposure;
        SaturationSlider.Value = values.Saturation;
        TemperatureSlider.Value = values.Temperature;
        TintSlider.Value = values.Tint;
        ShadowsSlider.Value = values.Shadows;
        HighlightsSlider.Value = values.Highlights;
        ClaritySlider.Value = values.Clarity;
        BlurSlider.Value = values.Blur;
        SharpenSlider.Value = values.Sharpen;
        VignetteSlider.Value = values.Vignette;
        NaturalizeBoundaryCheckBox.IsChecked = values.NaturalizeBoundary;

        Values = values;
        _isInitializing = false;

        RegisterSliderHandlers();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Values = BuildValues();
        IsConfirmed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RegisterSliderHandlers()
    {
        var sliders = new Slider[]
        {
            BrightnessSlider,
            ContrastSlider,
            GammaSlider,
            ExposureSlider,
            SaturationSlider,
            TemperatureSlider,
            TintSlider,
            ShadowsSlider,
            HighlightsSlider,
            ClaritySlider,
            BlurSlider,
            SharpenSlider,
            VignetteSlider
        };

        foreach (var slider in sliders)
            slider.ValueChanged += Slider_ValueChanged;
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing)
            return;

        Values = BuildValues();
        ValuesChanged?.Invoke(Values);
    }

    private void NaturalizeBoundaryCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        Values = BuildValues();
        ValuesChanged?.Invoke(Values);
    }

    private void ResetParameter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string parameter }) return;

        switch (parameter)
        {
            case nameof(MaskAdjustmentValues.Brightness): BrightnessSlider.Value = 0; break;
            case nameof(MaskAdjustmentValues.Contrast): ContrastSlider.Value = 0; break;
            case nameof(MaskAdjustmentValues.Gamma): GammaSlider.Value = 1.0; break;
            case nameof(MaskAdjustmentValues.Exposure): ExposureSlider.Value = 0; break;
            case nameof(MaskAdjustmentValues.Saturation): SaturationSlider.Value = 0; break;
            case nameof(MaskAdjustmentValues.Temperature): TemperatureSlider.Value = 0; break;
            case nameof(MaskAdjustmentValues.Tint): TintSlider.Value = 0; break;
            case nameof(MaskAdjustmentValues.Shadows): ShadowsSlider.Value = 0; break;
            case nameof(MaskAdjustmentValues.Highlights): HighlightsSlider.Value = 0; break;
            case nameof(MaskAdjustmentValues.Clarity): ClaritySlider.Value = 0; break;
            case nameof(MaskAdjustmentValues.Blur): BlurSlider.Value = 0; break;
            case nameof(MaskAdjustmentValues.Sharpen): SharpenSlider.Value = 0; break;
            case nameof(MaskAdjustmentValues.Vignette): VignetteSlider.Value = 0; break;
        }
    }

    private void DialogScrollViewer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
            return;

        var focused = Keyboard.FocusedElement as DependencyObject;
        var slider = FindParent<Slider>(focused);
        if (slider == null) return;

        Dispatcher.BeginInvoke(() => slider.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ValueTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox textBox) return;
        var grid = FindParent<Grid>(textBox);
        if (grid == null) return;

        foreach (var child in grid.Children.OfType<Slider>())
        {
            if (Grid.GetRow(child) == Grid.GetRow(textBox))
            {
                child.Focus();
                e.Handled = true;
                break;
            }
        }
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T t) return t;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private MaskAdjustmentValues BuildValues() =>
        new(
            BrightnessSlider.Value,
            ContrastSlider.Value,
            GammaSlider.Value,
            ExposureSlider.Value,
            SaturationSlider.Value,
            TemperatureSlider.Value,
            TintSlider.Value,
            ShadowsSlider.Value,
            HighlightsSlider.Value,
            ClaritySlider.Value,
            BlurSlider.Value,
            SharpenSlider.Value,
            VignetteSlider.Value,
            NaturalizeBoundaryCheckBox.IsChecked == true
        );
}
