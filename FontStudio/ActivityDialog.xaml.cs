using FontStudio.Services;
using System.Windows;

namespace FontStudio;

public partial class ActivityDialog : Window
{
    public ActivityDialog(IReadOnlyList<AuditEntry> entries)
    {
        InitializeComponent();
        ActivityGrid.ItemsSource = entries;
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
