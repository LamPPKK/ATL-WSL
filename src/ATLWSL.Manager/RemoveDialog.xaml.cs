using System.Windows;

namespace AtlWsl.Manager;

public enum RemoveChoice
{
    Cancel,
    RetainData,
    DeleteData,
}

public partial class RemoveDialog : Window
{
    public RemoveDialog(string displayName)
    {
        InitializeComponent();
        PromptText.Text = $"Choose what ATL-WSL should do with the isolated data for “{displayName}”.";
    }

    public RemoveChoice Choice { get; private set; } = RemoveChoice.Cancel;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = RemoveChoice.Cancel;
        DialogResult = false;
    }

    private void Retain_Click(object sender, RoutedEventArgs e)
    {
        Choice = RemoveChoice.RetainData;
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        Choice = RemoveChoice.DeleteData;
        DialogResult = true;
    }
}
