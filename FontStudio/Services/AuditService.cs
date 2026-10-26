using System.Text.Json;

namespace FontStudio.Services;

public sealed record AuditEntry(DateTimeOffset TimestampUtc, string Action, string Target, string Outcome, string Details);

public sealed class AuditService
{
    private readonly string _filePath;

    public AuditService(string? dataFolder = null)
    {
        var folder = dataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FontStudio");
        _filePath = Path.Combine(folder, "audit.jsonl");
    }

    public void Write(string action, string target, string outcome, string details)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var entry = new AuditEntry(DateTimeOffset.UtcNow, action, Path.GetFileName(target), outcome, details);
            File.AppendAllText(_filePath, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch
        {
            // Audit failure must not corrupt or mask the font operation result.
        }
    }

    public IReadOnlyList<AuditEntry> ReadRecent(int limit = 200)
    {
        try
        {
            if (!File.Exists(_filePath)) return [];
            return File.ReadLines(_filePath)
                .TakeLast(Math.Clamp(limit, 1, 1000))
                .Select(line => JsonSerializer.Deserialize<AuditEntry>(line))
                .Where(entry => entry is not null)
                .Cast<AuditEntry>()
                .Reverse()
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
