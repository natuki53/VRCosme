using System.Windows;

namespace VRCosme.Views;

public partial class MaskRenameDialog : Window
{
    public string MaskName => NameTextBox.Text.Trim();

    public MaskRenameDialog(string initialName)
    {
        InitializeComponent();
        NameTextBox.Text = initialName;
        NameTextBox.SelectAll();
        Loaded += (_, _) => NameTextBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(MaskName))
            return;

        DialogResult = true;
    }
}
