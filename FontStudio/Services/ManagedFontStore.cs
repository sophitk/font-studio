using System.Text.Json;

namespace FontStudio.Services;

public sealed record ManagedFontEntry(string SourceName, string InstalledPath, string RegistryName, string Sha256, DateTimeOffset InstalledAtUtc);

public sealed class ManagedFontStore
{
    private readonly string _filePath;

    public ManagedFontStore(string? dataFolder = null)
    {
        var folder = dataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FontStudio");
        _filePath = Path.Combine(folder, "managed-fonts.json");
    }

    public IReadOnlyList<ManagedFontEntry> Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return [];
            return JsonSerializer.Deserialize<List<ManagedFontEntry>>(File.ReadAllText(_filePath)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<ManagedFontEntry> LoadStrict()
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ManagedFontEntry>>(File.ReadAllText(_filePath))
                ?? throw new InvalidDataException("The managed-font manifest is empty.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "The managed-font manifest cannot be read safely. Font mutation remains locked until it is reviewed.", ex);
        }
    }

    public ManagedFontEntry? FindByPath(string path) => LoadStrict().FirstOrDefault(entry =>
        Path.GetFullPath(entry.InstalledPath).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    public void Add(ManagedFontEntry entry)
    {
        var entries = LoadStrict().Where(existing =>
            !Path.GetFullPath(existing.InstalledPath).Equals(Path.GetFullPath(entry.InstalledPath), StringComparison.OrdinalIgnoreCase)).ToList();
        entries.Add(entry);
        Save(entries);
    }

    public void Remove(string path)
    {
        var entries = LoadStrict().Where(entry =>
            !Path.GetFullPath(entry.InstalledPath).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)).ToList();
        Save(entries);
    }

    private void Save(IReadOnlyList<ManagedFontEntry> entries)
    {
        var folder = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(folder);
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _filePath, true);
    }
}
