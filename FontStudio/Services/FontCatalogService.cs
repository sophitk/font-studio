using FontStudio.Models;
using Microsoft.Win32;
using System.Windows.Markup;
using System.Windows.Media;

namespace FontStudio.Services;

public sealed class FontCatalogService
{
    private const string FontRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
    private readonly string _windowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    private readonly string _userFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
    private readonly ManagedFontStore _managedFonts = new();

    public IReadOnlyList<FontRecord> LoadInstalledFonts()
    {
        var managedByPath = _managedFonts.Load().ToDictionary(
            entry => Path.GetFullPath(entry.InstalledPath),
            entry => entry,
            StringComparer.OrdinalIgnoreCase);
        var sources = ReadRegistryFonts()
            .GroupBy(source => Path.GetFullPath(source.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => InspectSource(group.First(), managedByPath))
            .Where(source => source.Inspection?.IsValid == true && !string.IsNullOrWhiteSpace(source.Inspection.FamilyName))
            .ToList();
        var sourcesByFamily = sources
            .GroupBy(source => Normalize(source.Inspection!.FamilyName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var results = new List<FontRecord>();
        foreach (var family in Fonts.SystemFontFamilies.OrderBy(font => font.Source, StringComparer.CurrentCultureIgnoreCase))
        {
            var familyName = GetLocalizedName(family);
            sourcesByFamily.TryGetValue(Normalize(familyName), out var matches);
            matches ??= [];
            var exactSource = matches.Count == 1 ? matches[0] : null;
            var inspection = exactSource?.Inspection;
            var scope = matches.Count == 0
                ? "System"
                : matches.All(match => match.IsUser) ? "User"
                : matches.Any(match => match.IsUser) ? "Mixed"
                : "System";
            var sourcePath = exactSource?.Path ?? (matches.Count > 1 ? $"Multiple installed files ({matches.Count})" : "Windows font catalog");

            results.Add(new FontRecord
            {
                FamilyName = familyName,
                StyleName = inspection?.SubfamilyName is { Length: > 0 } style ? style : "Regular",
                SourcePath = sourcePath,
                Scope = scope,
                CanDeactivate = exactSource?.IsManaged == true,
                Version = inspection?.Version is { Length: > 0 } version ? version : "Unknown",
                TableCount = inspection?.TableCount ?? 0,
                IsVariable = matches.Any(match => match.Inspection?.IsVariable == true),
                HasColorGlyphs = matches.Any(match => match.Inspection?.HasColorGlyphs == true),
                IsSymbol = familyName.Contains("Symbol", StringComparison.OrdinalIgnoreCase) || familyName.Contains("Wingdings", StringComparison.OrdinalIgnoreCase)
            });
        }

        return results.GroupBy(font => font.FamilyName, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
    }

    public IReadOnlyList<string> InstalledFileHashes()
    {
        var hashes = new List<string>();
        foreach (var source in ReadRegistryFonts().Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var inspection = FontFileInspector.Inspect(source);
            if (inspection.IsValid) hashes.Add(inspection.Sha256);
        }
        return hashes;
    }

    private FontSource InspectSource(RegistryFont source, IReadOnlyDictionary<string, ManagedFontEntry> managedByPath)
    {
        var inspection = FontFileInspector.Inspect(source.Path);
        var fullPath = Path.GetFullPath(source.Path);
        var isManaged = managedByPath.TryGetValue(fullPath, out var managed) &&
                        inspection.IsValid &&
                        inspection.Sha256.Equals(managed.Sha256, StringComparison.OrdinalIgnoreCase) &&
                        source.RegistryName.Equals(managed.RegistryName, StringComparison.Ordinal);
        return new(source.RegistryName, fullPath, source.IsUser, isManaged, inspection);
    }

    private List<RegistryFont> ReadRegistryFonts()
    {
        var values = new List<RegistryFont>();
        ReadHive(Registry.LocalMachine, false);
        ReadHive(Registry.CurrentUser, true);
        return values;

        void ReadHive(RegistryKey hive, bool userScope)
        {
            using var key = hive.OpenSubKey(FontRegistryPath, false);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is not string stored || string.IsNullOrWhiteSpace(stored)) continue;
                var path = Path.IsPathRooted(stored)
                    ? stored
                    : Path.Combine(userScope ? _userFonts : _windowsFonts, stored);
                if (!File.Exists(path)) continue;
                values.Add(new RegistryFont(name, path, userScope));
            }
        }
    }

    private static string GetLocalizedName(FontFamily family)
    {
        var language = XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag);
        return family.FamilyNames.TryGetValue(language, out var localized) ? localized : family.Source;
    }

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private sealed record RegistryFont(string RegistryName, string Path, bool IsUser);
    private sealed record FontSource(string RegistryName, string Path, bool IsUser, bool IsManaged, FontFileInspection? Inspection);
}
