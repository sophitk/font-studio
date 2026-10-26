using FontStudio.Services;
using System.Windows;

namespace FontStudio;

public partial class UtilitiesWindow : Window
{
    private readonly DuplicateFinderService _duplicateFinder = new();

    public UtilitiesWindow()
    {
        InitializeComponent();
    }

    private async void ScanDuplicates_Click(object sender, RoutedEventArgs e)
    {
        ScanDuplicatesButton.IsEnabled = false;
        DuplicateProgress.Visibility = Visibility.Visible;
        DuplicateSummary.Text = "Scanning registered TTF, OTF and TTC files...";
        try
        {
            var result = await Task.Run(_duplicateFinder.Scan);
            DuplicateGrid.ItemsSource = result.Rows;
            DuplicateSummary.Text = $"Scanned {result.FilesScanned:N0} files - {result.ExactGroups:N0} exact duplicate groups - {result.FaceGroups:N0} same-face groups - {result.InvalidFiles:N0} unreadable/invalid";
        }
        catch (Exception ex)
        {
            DuplicateSummary.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            DuplicateProgress.Visibility = Visibility.Collapsed;
            ScanDuplicatesButton.IsEnabled = true;
        }
    }
}
