using FontStudio.Models;
using Microsoft.Win32;

namespace FontStudio.Services;

public sealed class DuplicateFinderService
{
    private const string FontRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
    private readonly string _windowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    private readonly string _userFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");

    public DuplicateScanResult Scan()
    {
        var paths = ReadRegisteredFontPaths();
        var inspected = new List<InspectedFont>();
        var invalidFiles = 0;
        foreach (var path in paths)
        {
            var inspection = FontFileInspector.Inspect(path);
            if (!inspection.IsValid)
            {
                invalidFiles++;
                continue;
            }
            inspected.Add(new InspectedFont(path, inspection));
        }

        var rows = new List<DuplicateFontRow>();
        var exactGroups = inspected
            .GroupBy(item => item.Inspection.Sha256, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .OrderByDescending(group => group.Count())
            .ToList();
        var groupNumber = 1;
        foreach (var group in exactGroups)
        {
            foreach (var item in group.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase))
                rows.Add(ToRow(item, $"Exact #{groupNumber}", "Exact file"));
            groupNumber++;
        }

        var faceGroups = inspected
            .Where(item => !string.IsNullOrWhiteSpace(item.Inspection.FamilyName))
            .GroupBy(item => FaceKey(item.Inspection), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Inspection.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        groupNumber = 1;
        foreach (var group in faceGroups)
        {
            foreach (var item in group.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase))
                rows.Add(ToRow(item, $"Face #{groupNumber}", "Same face metadata"));
            groupNumber++;
        }

        return new(rows, paths.Count, exactGroups.Count, faceGroups.Count, invalidFiles);
    }

    private List<string> ReadRegisteredFontPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadHive(Registry.LocalMachine, false);
        ReadHive(Registry.CurrentUser, true);
        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();

        void ReadHive(RegistryKey hive, bool userScope)
        {
            using var key = hive.OpenSubKey(FontRegistryPath, false);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is not string value || string.IsNullOrWhiteSpace(value)) continue;
                var path = Path.IsPathRooted(value) ? value : Path.Combine(userScope ? _userFonts : _windowsFonts, value);
                if (File.Exists(path) && IsSupported(path)) paths.Add(Path.GetFullPath(path));
            }
        }
    }

    private static bool IsSupported(string path) => Path.GetExtension(path) is var extension &&
        (extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".otf", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase));

    private static string FaceKey(FontFileInspection inspection) => string.Join('|',
        Normalize(inspection.FamilyName), Normalize(inspection.SubfamilyName), Normalize(inspection.Version));

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static DuplicateFontRow ToRow(InspectedFont item, string group, string type) => new(
        group,
        type,
        string.IsNullOrWhiteSpace(item.Inspection.FamilyName) ? Path.GetFileNameWithoutExtension(item.Path) : item.Inspection.FamilyName,
        string.IsNullOrWhiteSpace(item.Inspection.SubfamilyName) ? "Unknown" : item.Inspection.SubfamilyName,
        string.IsNullOrWhiteSpace(item.Inspection.Version) ? "Unknown" : item.Inspection.Version,
        item.Path,
        item.Inspection.SizeBytes,
        item.Inspection.Sha256);

    private sealed record InspectedFont(string Path, FontFileInspection Inspection);
}
