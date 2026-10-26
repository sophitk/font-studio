using System.Text.Json;

namespace FontStudio.Services;

public enum FontMutationKind
{
    Install,
    Deactivate
}

public enum FontMutationStep
{
    Prepared,
    FileCopied,
    RegistryWritten,
    ResourceChanged,
    ManifestChanged,
    FileQuarantined,
    RegistryRemoved,
    BroadcastCompleted,
    QuarantineDeleted
}

public sealed record FontMutationTransaction(
    Guid TransactionId,
    FontMutationKind Kind,
    FontMutationStep Step,
    string SourceName,
    string InstalledPath,
    string RegistryName,
    string Sha256,
    string? QuarantinePath,
    DateTimeOffset InstalledAtUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed class FontMutationJournalService
{
    private static readonly object JournalLock = new();
    private readonly string _filePath;

    public FontMutationJournalService(string? dataFolder = null)
    {
        var folder = dataFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FontStudio");
        _filePath = Path.Combine(folder, "pending-mutation.json");
    }

    public string FilePath => _filePath;

    public FontMutationTransaction? LoadPending()
    {
        lock (JournalLock)
        {
            if (!File.Exists(_filePath)) return null;
            try
            {
                return JsonSerializer.Deserialize<FontMutationTransaction>(File.ReadAllText(_filePath))
                    ?? throw new InvalidDataException("The pending mutation journal is empty.");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(
                    "The pending mutation journal cannot be read. Mutation remains locked until it is reviewed.", ex);
            }
        }
    }

    public FontMutationTransaction BeginInstall(ManagedFontEntry entry)
    {
        return Begin(new FontMutationTransaction(
            Guid.NewGuid(), FontMutationKind.Install, FontMutationStep.Prepared,
            entry.SourceName, Path.GetFullPath(entry.InstalledPath), entry.RegistryName,
            entry.Sha256, null, entry.InstalledAtUtc, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    public FontMutationTransaction BeginDeactivate(ManagedFontEntry entry, string quarantinePath)
    {
        return Begin(new FontMutationTransaction(
            Guid.NewGuid(), FontMutationKind.Deactivate, FontMutationStep.Prepared,
            entry.SourceName, Path.GetFullPath(entry.InstalledPath), entry.RegistryName,
            entry.Sha256, Path.GetFullPath(quarantinePath), entry.InstalledAtUtc,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    public FontMutationTransaction Advance(FontMutationTransaction transaction, FontMutationStep step)
    {
        var updated = transaction with { Step = step, UpdatedAtUtc = DateTimeOffset.UtcNow };
        Save(updated, requireNoExistingTransaction: false);
        return updated;
    }

    public void Clear(Guid transactionId)
    {
        lock (JournalLock)
        {
            if (!File.Exists(_filePath)) return;
            var pending = LoadPending();
            if (pending?.TransactionId != transactionId)
                throw new InvalidOperationException("The mutation journal belongs to a different transaction and was not cleared.");
            File.Delete(_filePath);
        }
    }

    private FontMutationTransaction Begin(FontMutationTransaction transaction)
    {
        Save(transaction, requireNoExistingTransaction: true);
        return transaction;
    }

    private void Save(FontMutationTransaction transaction, bool requireNoExistingTransaction)
    {
        lock (JournalLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            if (requireNoExistingTransaction && File.Exists(_filePath))
                throw new InvalidOperationException(
                    "A pending font mutation must be recovered before another mutation can start.");

            if (!requireNoExistingTransaction && File.Exists(_filePath))
            {
                var pending = LoadPending();
                if (pending?.TransactionId != transaction.TransactionId)
                    throw new InvalidOperationException("The mutation journal belongs to a different transaction.");
            }

            var tempPath = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(transaction, new JsonSerializerOptions { WriteIndented = true });
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tempPath, _filePath, true);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
    }
}
