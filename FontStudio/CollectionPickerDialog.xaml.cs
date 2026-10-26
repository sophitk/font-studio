using FontStudio.Models;
using System.Collections.ObjectModel;
using System.Windows;

namespace FontStudio;

public partial class CollectionPickerDialog : Window
{
    public FontCollection? SelectedCollection => CollectionList.SelectedItem as FontCollection;
    public CollectionPickerDialog(ObservableCollection<FontCollection> collections)
    {
        InitializeComponent();
        CollectionList.ItemsSource = collections;
        CollectionList.SelectedIndex = 0;
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCollection is not null) DialogResult = true;
    }
}
