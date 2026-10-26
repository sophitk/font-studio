using FontStudio.Models;
using FontStudio.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace FontStudio;

public partial class MainWindow : Window
{
    private readonly FontCatalogService _catalog = new();
    private readonly FontActivationService _activation = new();
    private readonly LibraryService _libraryService = new();
    private readonly PilotPolicy _policy = PilotPolicy.Load();
    private readonly ObservableCollection<FontRecord> _visibleFonts = [];
    private readonly ObservableCollection<FontCollection> _collections = [];
    private List<FontRecord> _allFonts = [];
    private LibraryState _state = new();
    private string _filter = "all";
    private bool _mutationSessionApproved;

    private bool CanMutate => _policy.AllowFontChanges && (!_policy.RequiresSessionConfirmation || _mutationSessionApproved);

    public MainWindow()
    {
        InitializeComponent();
        FontList.ItemsSource = _visibleFonts;
        CollectionsList.ItemsSource = _collections;
        UpdateMutationControls();
        SizeSlider.ValueChanged += (_, _) => LargePreview.FontSize = SizeSlider.Value;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        FontRecoveryResult? recovery = null;
        if (_policy.RequiresSessionConfirmation)
        {
            var decision = MessageBox.Show(this,
                "FontStudio can install and deactivate fonts for your current Windows user. It changes only your user-font folder and HKCU font registration; system fonts are never removed.\n\n" +
                "Startup recovery and rollback are enabled. Reboot persistence, power loss, disk-full recovery, and multi-user sessions have limited validation.\n\n" +
                "Enable font changes and recover any interrupted FontStudio operation for this app session?",
                "Enable current-user font management", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (decision == MessageBoxResult.Yes)
            {
                try
                {
                    recovery = await Task.Run(() => new FontRecoveryService().RecoverPending());
                    _mutationSessionApproved = true;
                }
                catch (Exception ex)
                {
                    _mutationSessionApproved = false;
                    MessageBox.Show(this, ex.Message,
                        "Recovery failed - font changes remain locked",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        UpdateMutationControls();
        await LoadCatalogAsync();
        if (_policy.RequiresSessionConfirmation)
        {
            StatusText.Text = !_mutationSessionApproved
                ? "Font changes remain locked for this session"
                : recovery?.HadPendingMutation == true
                    ? $"Font management enabled; recovered transaction {recovery.TransactionId}"
                    : "Font management enabled; no pending mutation found";
        }
    }

    private void UpdateMutationControls()
    {
        InstallFontButton.IsEnabled = CanMutate;
        InstallFontButton.ToolTip = CanMutate
            ? "Install for the current Windows user"
            : _policy.RequiresSessionConfirmation ? "Font changes are locked for this session" : "Disabled by read-only pilot policy";
        SafetyModeText.Text = _policy.RequiresSessionConfirmation
            ? (_mutationSessionApproved ? "Current-user changes - enabled" : "Current-user changes - locked")
            : _policy.AllowFontChanges ? "Current-user changes - confirmation required" : _policy.Mode;
        if (FontList.SelectedItem is FontRecord font)
            DeactivateButton.IsEnabled = CanMutate && font.CanDeactivate;
    }

    private async Task LoadCatalogAsync()
    {
        StatusText.Text = "Scanning the Windows font catalog…";
        _state = _libraryService.Load();
        PreviewTextBox.Text = _state.PreviewText;
        _collections.Clear();
        foreach (var collection in _state.Collections) _collections.Add(collection);
        _allFonts = (await Task.Run(_catalog.LoadInstalledFonts)).ToList();
        foreach (var font in _allFonts) font.IsFavorite = _state.Favorites.Contains(font.FamilyName);
        ApplyFilter();
        StatusText.Text = $"Loaded {_allFonts.Count:N0} font families";
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        IEnumerable<FontRecord> fonts = _allFonts;
        fonts = _filter switch
        {
            "favorites" => fonts.Where(f => f.IsFavorite),
            "user" => fonts.Where(f => f.Scope == "User"),
            "system" => fonts.Where(f => f.Scope == "System"),
            "variable" => fonts.Where(f => f.IsVariable),
            _ when _filter.StartsWith("collection:") => FilterCollection(fonts, _filter[11..]),
            _ => fonts
        };
        if (!string.IsNullOrWhiteSpace(query))
            fonts = fonts.Where(f => f.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        _visibleFonts.Clear();
        foreach (var font in fonts.OrderBy(f => f.FamilyName, StringComparer.CurrentCultureIgnoreCase)) _visibleFonts.Add(font);
        CountText.Text = $"{_visibleFonts.Count:N0}";
    }

    private IEnumerable<FontRecord> FilterCollection(IEnumerable<FontRecord> fonts, string name)
    {
        var collection = _collections.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return collection is null ? [] : fonts.Where(f => collection.Families.Contains(f.FamilyName));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (LibraryList.SelectedItem is not ListBoxItem item) return;
        CollectionsList.SelectedItem = null;
        _filter = item.Tag?.ToString() ?? "all";
        ListTitle.Text = item.Content?.ToString() ?? "Fonts";
        if (IsLoaded) ApplyFilter();
    }

    private void CollectionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (CollectionsList.SelectedItem is not FontCollection collection) return;
        LibraryList.SelectedItem = null;
        _filter = "collection:" + collection.Name;
        ListTitle.Text = collection.Name;
        ApplyFilter();
    }

    private void FontList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontList.SelectedItem is not FontRecord font)
        {
            FavoriteButton.IsEnabled = AddToCollectionButton.IsEnabled = DeactivateButton.IsEnabled = false;
            return;
        }
        SelectedName.Text = font.DisplayName;
        LargePreview.FontFamily = GlyphPreview.FontFamily = font.PreviewFamily;
        LargePreview.Text = PreviewTextBox.Text;
        FormatText.Text = font.Format;
        StyleText.Text = font.StyleName;
        VersionText.Text = font.Version;
        FeaturesText.Text = string.IsNullOrWhiteSpace(font.FeatureSummary) ? "Standard OpenType" : font.FeatureSummary;
        LocationText.Text = font.SourcePath;
        FavoriteButton.Content = font.IsFavorite ? "★ Favorite" : "☆ Favorite";
        FavoriteButton.IsEnabled = AddToCollectionButton.IsEnabled = true;
        DeactivateButton.IsEnabled = CanMutate && font.CanDeactivate;
    }

    private void PreviewTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LargePreview is null) return;
        LargePreview.Text = PreviewTextBox.Text;
        if (!IsLoaded) return;
        _state.PreviewText = PreviewTextBox.Text;
        SaveState();
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (FontList.SelectedItem is not FontRecord font) return;
        font.IsFavorite = !font.IsFavorite;
        if (font.IsFavorite) _state.Favorites.Add(font.FamilyName); else _state.Favorites.Remove(font.FamilyName);
        FavoriteButton.Content = font.IsFavorite ? "★ Favorite" : "☆ Favorite";
        SaveState();
        if (_filter == "favorites") ApplyFilter();
    }

    private void NewCollection_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NameDialog { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.CollectionName)) return;
        if (_collections.Any(c => c.Name.Equals(dialog.CollectionName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A collection with that name already exists.", "FontStudio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var collection = new FontCollection { Name = dialog.CollectionName.Trim() };
        _collections.Add(collection);
        _state.Collections = _collections.ToList();
        SaveState();
    }

    private void AddToCollection_Click(object sender, RoutedEventArgs e)
    {
        if (FontList.SelectedItem is not FontRecord font) return;
        if (_collections.Count == 0)
        {
            MessageBox.Show(this, "Create a collection first.", "FontStudio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new CollectionPickerDialog(_collections) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedCollection is null) return;
        dialog.SelectedCollection.Families.Add(font.FamilyName);
        SaveState();
        StatusText.Text = $"Added {font.FamilyName} to {dialog.SelectedCollection.Name}";
    }

    private async void ValidateFile_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseFontFile();
        if (path is null) return;
        var result = FontFileInspector.Inspect(path);
        StatusText.Text = "Checking installed font hashes…";
        var installedHashes = await Task.Run(_catalog.InstalledFileHashes);
        var duplicate = result.IsValid && installedHashes.Contains(result.Sha256, StringComparer.OrdinalIgnoreCase);
        var message = result.IsValid
            ? $"Valid {result.Format} font\nFamily: {Fallback(result.FamilyName)}\nStyle: {Fallback(result.SubfamilyName)}\nVersion: {Fallback(result.Version)}\nTables: {result.TableCount}\nVariable: {(result.IsVariable ? "Yes" : "No")}\nColor glyphs: {(result.HasColorGlyphs ? "Yes" : "No")}\nSize: {result.SizeBytes / 1024d:N1} KB\nSHA-256: {result.Sha256}\nExact duplicate installed: {(duplicate ? "Yes" : "No")}"
            : result.Message;
        MessageBox.Show(this, message, "Font validation", MessageBoxButton.OK, result.IsValid ? MessageBoxImage.Information : MessageBoxImage.Warning);
        StatusText.Text = result.IsValid ? "Font structure check completed" : "Font structure check failed";
    }

    private async void InstallFont_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutate)
        {
            MessageBox.Show(this, "Font installation is disabled in this read-only pilot build.", "FontStudio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var path = ChooseFontFile();
        if (path is null) return;
        var result = FontFileInspector.Inspect(path);
        if (!result.IsValid)
        {
            MessageBox.Show(this, result.Message, "FontStudio", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        StatusText.Text = "Checking for an exact installed duplicate…";
        var installedHashes = await Task.Run(_catalog.InstalledFileHashes);
        var duplicate = installedHashes.Contains(result.Sha256, StringComparer.OrdinalIgnoreCase);
        if (duplicate)
        {
            MessageBox.Show(this, "This exact font file is already installed.", "FontStudio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var confirm = MessageBox.Show(this,
            $"Install {Path.GetFileName(path)} for the current Windows user?\n\nOnly continue if you trust the source of this font. This copies the file to your user font folder and registers it with Windows.",
            "Confirm font installation", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var destination = await Task.Run(() => _activation.ActivateForCurrentUser(path));
            await LoadCatalogAsync();
            StatusText.Text = $"Installed {Path.GetFileName(destination)} for the current user";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Installation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Deactivate_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutate)
        {
            MessageBox.Show(this, "Font deactivation is disabled in this read-only pilot build.", "FontStudio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (FontList.SelectedItem is not FontRecord font || !font.CanDeactivate) return;
        var confirm = MessageBox.Show(this,
            $"Deactivate {font.FamilyName} and remove its current-user font file?\n\nDocuments that use this font may display differently.",
            "Confirm deactivation", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(() => _activation.DeactivateCurrentUserFont(font.SourcePath));
            await LoadCatalogAsync();
            StatusText.Text = $"Deactivated {font.FamilyName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Deactivation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? ChooseFontFile()
    {
        var dialog = new OpenFileDialog { Filter = "Font files (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc", CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void Activity_Click(object sender, RoutedEventArgs e)
    {
        new ActivityDialog(new AuditService().ReadRecent()) { Owner = this }.ShowDialog();
    }

    private void Utilities_Click(object sender, RoutedEventArgs e)
    {
        new UtilitiesWindow { Owner = this }.ShowDialog();
    }

    private static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "Not declared" : value;

    private void SaveState()
    {
        _state.Collections = _collections.ToList();
        _libraryService.Save(_state);
    }
}
