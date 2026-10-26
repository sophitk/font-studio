using FontStudio.Models;
using System.Text.Json;

namespace FontStudio.Services;

public sealed class LibraryService
{
    private readonly string _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FontStudio");
    private string FilePath => Path.Combine(_folder, "library.json");

    public LibraryState Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public void Save(LibraryState state)
    {
        Directory.CreateDirectory(_folder);
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, true);
    }
}

public sealed class LibraryState
{
    public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FontCollection> Collections { get; set; } = [];
    public string PreviewText { get; set; } = "The quick brown fox jumps over the lazy dog. 0123456789";
}
