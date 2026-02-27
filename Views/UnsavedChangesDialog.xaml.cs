using System.Windows;

namespace VRCosme.Views;

public enum UnsavedChangesDialogResult
{
    Cancel,
    Save,
    Discard
}

public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialogResult Result { get; private set; } = UnsavedChangesDialogResult.Cancel;

    public UnsavedChangesDialog()
    {
        InitializeComponent();
    }

    private void SaveAndContinue_Click(object sender, RoutedEventArgs e)
    {
        Result = UnsavedChangesDialogResult.Save;
        DialogResult = true;
    }

    private void DiscardAndContinue_Click(object sender, RoutedEventArgs e)
    {
        Result = UnsavedChangesDialogResult.Discard;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = UnsavedChangesDialogResult.Cancel;
        DialogResult = false;
    }
}
