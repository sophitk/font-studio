namespace FontStudio.Models;

public sealed class FontCollection
{
    public required string Name { get; set; }
    public HashSet<string> Families { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
