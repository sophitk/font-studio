using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace FontStudio.Models;

public sealed class FontRecord : INotifyPropertyChanged
{
    private bool _isFavorite;

    public required string FamilyName { get; init; }
    public string StyleName { get; init; } = "Regular";
    public string SourcePath { get; init; } = "Windows font catalog";
    public string Scope { get; init; } = "System";
    public string Version { get; init; } = "Unknown";
    public int TableCount { get; init; }
    public bool IsVariable { get; init; }
    public bool HasColorGlyphs { get; init; }
    public bool IsSymbol { get; init; }
    public bool CanDeactivate { get; init; }
    public FontFamily PreviewFamily => new(FamilyName);
    public string DisplayName => StyleName.Equals("Regular", StringComparison.OrdinalIgnoreCase)
        ? FamilyName
        : $"{FamilyName} {StyleName}";
    public string Format => Path.GetExtension(SourcePath).TrimStart('.').ToUpperInvariant() is { Length: > 0 } value
        ? value
        : "Installed";
    public string StatusText => CanDeactivate ? "User font · managed" : $"{Scope} font";
    public string FeatureSummary => string.Join(" · ", new[]
    {
        IsVariable ? "Variable" : null,
        HasColorGlyphs ? "Color glyphs" : null,
        TableCount > 0 ? $"{TableCount} tables" : null
    }.Where(value => value is not null));

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
