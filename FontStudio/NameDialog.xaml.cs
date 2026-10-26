using System.Windows;

namespace FontStudio;

public partial class NameDialog : Window
{
    public string CollectionName => NameBox.Text;
    public NameDialog() => InitializeComponent();
    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) return;
        DialogResult = true;
    }
}
