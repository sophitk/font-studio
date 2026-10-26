using FontStudio.Models;
using FontStudio.Services;

var failures = new List<string>();

Check(!FontFileInspector.Inspect("Z:\\not-a-real-font.ttf").IsValid, "missing file is rejected");

var tempFile = Path.Combine(Path.GetTempPath(), $"fontstudio-{Guid.NewGuid():N}.txt");
var malformedFont = Path.Combine(Path.GetTempPath(), $"fontstudio-{Guid.NewGuid():N}.ttf");
try
{
    File.WriteAllText(tempFile, "not a font");
    Check(!FontFileInspector.Inspect(tempFile).IsValid, "unsupported extension is rejected");
    File.WriteAllBytes(malformedFont, [0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00]);
    Check(!FontFileInspector.Inspect(malformedFont).IsValid, "truncated table directory is rejected");
}
finally
{
    if (File.Exists(tempFile)) File.Delete(tempFile);
    if (File.Exists(malformedFont)) File.Delete(malformedFont);
}

var fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
var installedFont = Directory.EnumerateFiles(fontsFolder)
    .FirstOrDefault(path => new[] { ".ttf", ".otf", ".ttc" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
Check(installedFont is not null, "Windows has at least one supported font file");
if (installedFont is not null)
{
    var inspection = FontFileInspector.Inspect(installedFont);
    Check(inspection.IsValid, $"installed font validates: {Path.GetFileName(installedFont)}");
    Check(inspection.TableCount > 0, "OpenType table directory is parsed");
    Check(!string.IsNullOrWhiteSpace(inspection.FamilyName), "font family metadata is parsed");
}

var catalog = new FontCatalogService().LoadInstalledFonts();
Check(catalog.Count > 0, "font catalog returns installed families");
Check(catalog.All(font => !string.IsNullOrWhiteSpace(font.FamilyName)), "catalog family names are populated");

var duplicateScan = new DuplicateFinderService().Scan();
Check(duplicateScan.FilesScanned > 0, "duplicate finder scans registered font files");
Check(duplicateScan.Rows.All(row => !string.IsNullOrWhiteSpace(row.Group) && row.Sha256.Length == 64), "duplicate finder rows are complete");

var pilotPolicy = PilotPolicy.Load();
Check(!pilotPolicy.AllowFontChanges, "packaged pilot policy disables font changes by default");
try
{
    _ = new FontActivationService().ActivateForCurrentUser("Z:\\not-a-real-font.ttf");
    Check(false, "activation service enforces read-only policy");
}
catch (InvalidOperationException)
{
    Check(true, "activation service enforces read-only policy");
}
try
{
    new PilotPolicy(true, "Disposable VM mutation test only").RequireFontChangesEnabled();
    Check(false, "VM mutation policy refuses host execution");
}
catch (InvalidOperationException)
{
    Check(true, "VM mutation policy refuses host execution");
}

try
{
    new PilotPolicy(true, "Current-user font management").RequireFontChangesEnabled();
    Check(false, "current-user mutation policy refuses a default read-only build");
}
catch (InvalidOperationException)
{
    Check(true, "current-user mutation policy refuses a default read-only build");
}

var testData = Path.Combine(Path.GetTempPath(), $"fontstudio-tests-{Guid.NewGuid():N}");
try
{
    var store = new ManagedFontStore(testData);
    var entry = new ManagedFontEntry("sample.ttf", Path.Combine(testData, "sample.ttf"), "Sample (FontStudio TTF)", "A1B2", DateTimeOffset.UtcNow);
    store.Add(entry);
    Check(store.FindByPath(entry.InstalledPath)?.Sha256 == "A1B2", "managed-font manifest round-trips");
    store.Remove(entry.InstalledPath);
    Check(store.FindByPath(entry.InstalledPath) is null, "managed-font manifest removes entries");

    var audit = new AuditService(testData);
    audit.Write("Test", "sample.ttf", "Success", "smoke");
    Check(audit.ReadRecent().Count == 1, "audit history round-trips");

    RunMutationJournalTests(Path.Combine(testData, "journal"));
    RunRecoveryTests(Path.Combine(testData, "recovery"));
}
finally
{
    if (Directory.Exists(testData)) Directory.Delete(testData, true);
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAILED ({failures.Count})");
    foreach (var failure in failures) Console.Error.WriteLine("- " + failure);
    return 1;
}

Console.WriteLine("PASS: FontStudio smoke tests");
return 0;

void Check(bool condition, string name)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {name}");
    if (!condition) failures.Add(name);
}

void RunMutationJournalTests(string folder)
{
    var installedPath = Path.Combine(folder, "Durable.ttf");
    var entry = new ManagedFontEntry(
        "Durable.ttf", installedPath, "Durable (FontStudio TTF)", "D00DFEED", DateTimeOffset.UtcNow);
    var journal = new FontMutationJournalService(folder);
    var transaction = journal.BeginInstall(entry);

    var reloaded = new FontMutationJournalService(folder).LoadPending();
    Check(reloaded is not null &&
        reloaded.TransactionId == transaction.TransactionId &&
        reloaded.Kind == FontMutationKind.Install &&
        reloaded.Step == FontMutationStep.Prepared &&
        reloaded.InstalledPath == Path.GetFullPath(installedPath),
        "mutation journal durably round-trips a pending transaction");

    try
    {
        journal.BeginInstall(entry);
        Check(false, "mutation journal locks out a second pending transaction");
    }
    catch (InvalidOperationException)
    {
        Check(true, "mutation journal locks out a second pending transaction");
    }

    var advanced = journal.Advance(transaction, FontMutationStep.RegistryWritten);
    var advancedReloaded = new FontMutationJournalService(folder).LoadPending();
    Check(advanced.Step == FontMutationStep.RegistryWritten &&
        advancedReloaded?.Step == FontMutationStep.RegistryWritten &&
        advancedReloaded.TransactionId == transaction.TransactionId,
        "mutation journal durably advances the active transaction");

    try
    {
        journal.Clear(Guid.NewGuid());
        Check(false, "mutation journal refuses to clear a different transaction");
    }
    catch (InvalidOperationException)
    {
        Check(File.Exists(journal.FilePath), "mutation journal refuses to clear a different transaction");
    }

    journal.Clear(transaction.TransactionId);
    Check(new FontMutationJournalService(folder).LoadPending() is null,
        "mutation journal clears only the matching transaction");
}

void RunRecoveryTests(string root)
{
    TestIncompleteInstallRollback(Path.Combine(root, "install-rollback"));
    TestDeactivateQuarantineRestore(Path.Combine(root, "deactivate-restore"));
    TestDeactivateCompletedForward(Path.Combine(root, "deactivate-forward"));
    TestHashMismatchStaysLocked(Path.Combine(root, "hash-mismatch"));
    TestRegistryCollisionStaysLocked(Path.Combine(root, "registry-collision"));
}

void TestIncompleteInstallRollback(string folder)
{
    var entry = RecoveryEntry(folder, "Incomplete.ttf", "AAA111");
    var journal = new FontMutationJournalService(folder);
    var transaction = journal.BeginInstall(entry);
    journal.Advance(transaction, FontMutationStep.ResourceChanged);
    var platform = new FakeFontRecoveryPlatform();
    platform.SeedFile(entry.InstalledPath, entry.Sha256);
    platform.Registry[entry.RegistryName] = entry.InstalledPath;
    platform.Managed[entry.InstalledPath] = entry;

    var result = new FontRecoveryService(journal, platform, new AuditService(folder)).RecoverPending();

    Check(result.Outcome == "Recovered" &&
        !platform.Files.ContainsKey(entry.InstalledPath) &&
        !platform.Registry.ContainsKey(entry.RegistryName) &&
        !platform.Managed.ContainsKey(entry.InstalledPath) &&
        platform.Operations.Contains("RemoveResource") &&
        platform.Operations.Contains("Broadcast") &&
        journal.LoadPending() is null,
        "recovery rolls back an incomplete install and clears its journal");
}

void TestDeactivateQuarantineRestore(string folder)
{
    var entry = RecoveryEntry(folder, "Quarantined.ttf", "BBB222");
    var quarantinePath = entry.InstalledPath + ".quarantine";
    var journal = new FontMutationJournalService(folder);
    journal.Advance(journal.BeginDeactivate(entry, quarantinePath), FontMutationStep.FileQuarantined);
    var platform = new FakeFontRecoveryPlatform();
    platform.SeedFile(quarantinePath, entry.Sha256);

    var result = new FontRecoveryService(journal, platform, new AuditService(folder)).RecoverPending();

    Check(result.Outcome == "Recovered" &&
        platform.Files.TryGetValue(entry.InstalledPath, out var restoredHash) && restoredHash == entry.Sha256 &&
        !platform.Files.ContainsKey(quarantinePath) &&
        platform.Registry.GetValueOrDefault(entry.RegistryName) == entry.InstalledPath &&
        platform.Managed.ContainsKey(entry.InstalledPath) &&
        platform.Operations.Contains("MoveFile") &&
        platform.Operations.Contains("AddResource") &&
        journal.LoadPending() is null,
        "recovery restores a quarantined font to the installed state");
}

void TestDeactivateCompletedForward(string folder)
{
    var entry = RecoveryEntry(folder, "Removed.ttf", "CCC333");
    var quarantinePath = entry.InstalledPath + ".quarantine";
    var journal = new FontMutationJournalService(folder);
    journal.Advance(journal.BeginDeactivate(entry, quarantinePath), FontMutationStep.RegistryRemoved);
    var platform = new FakeFontRecoveryPlatform();
    platform.Registry[entry.RegistryName] = entry.InstalledPath;
    platform.Managed[entry.InstalledPath] = entry;

    var result = new FontRecoveryService(journal, platform, new AuditService(folder)).RecoverPending();

    Check(result.Outcome == "Recovered" &&
        !platform.Registry.ContainsKey(entry.RegistryName) &&
        !platform.Managed.ContainsKey(entry.InstalledPath) &&
        !platform.Operations.Contains("MoveFile") &&
        platform.Operations.Contains("Broadcast") &&
        journal.LoadPending() is null,
        "recovery completes deactivation forward when both font files are absent");
}

void TestHashMismatchStaysLocked(string folder)
{
    var entry = RecoveryEntry(folder, "Changed.ttf", "EXPECTED");
    var journal = new FontMutationJournalService(folder);
    var transaction = journal.BeginInstall(entry);
    var platform = new FakeFontRecoveryPlatform();
    platform.SeedFile(entry.InstalledPath, "UNEXPECTED");

    try
    {
        new FontRecoveryService(journal, platform, new AuditService(folder)).RecoverPending();
        Check(false, "recovery hash mismatch keeps the transaction locked without destructive actions");
    }
    catch (InvalidOperationException)
    {
        var pending = journal.LoadPending();
        Check(pending?.TransactionId == transaction.TransactionId &&
            platform.Files.ContainsKey(entry.InstalledPath) &&
            !platform.Operations.Any(operation => operation is
                "RemoveResource" or "DeleteRegistry" or "RemoveManaged" or "DeleteFile" or "MoveFile" or "SetRegistry"),
            "recovery hash mismatch keeps the transaction locked without destructive actions");
    }
}

void TestRegistryCollisionStaysLocked(string folder)
{
    var entry = RecoveryEntry(folder, "Collision.ttf", "DDD444");
    var journal = new FontMutationJournalService(folder);
    var transaction = journal.BeginDeactivate(entry, entry.InstalledPath + ".quarantine");
    var platform = new FakeFontRecoveryPlatform();
    platform.SeedFile(entry.InstalledPath, entry.Sha256);
    var conflictingPath = Path.Combine(folder, "Different.ttf");
    platform.Registry[entry.RegistryName] = conflictingPath;

    try
    {
        new FontRecoveryService(journal, platform, new AuditService(folder)).RecoverPending();
        Check(false, "recovery registry-name collision blocks overwrite and keeps the journal");
    }
    catch (InvalidOperationException)
    {
        Check(platform.Registry[entry.RegistryName] == conflictingPath &&
            !platform.Operations.Contains("SetRegistry") &&
            journal.LoadPending()?.TransactionId == transaction.TransactionId,
            "recovery registry-name collision blocks overwrite and keeps the journal");
    }
}

ManagedFontEntry RecoveryEntry(string folder, string fileName, string sha256) => new(
    fileName,
    Path.GetFullPath(Path.Combine(folder, fileName)),
    $"{Path.GetFileNameWithoutExtension(fileName)} (FontStudio TTF)",
    sha256,
    DateTimeOffset.UtcNow);

sealed class FakeFontRecoveryPlatform : IFontRecoveryPlatform
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ManagedFontEntry> Managed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Operations { get; } = [];

    public void SeedFile(string path, string sha256) => Files[Path.GetFullPath(path)] = sha256;
    public bool FileExists(string path) => Files.ContainsKey(Path.GetFullPath(path));
    public string FileSha256(string path) => Files[Path.GetFullPath(path)];

    public void MoveFile(string source, string destination)
    {
        Operations.Add("MoveFile");
        var sourcePath = Path.GetFullPath(source);
        var destinationPath = Path.GetFullPath(destination);
        if (Files.ContainsKey(destinationPath)) throw new IOException("Destination already exists.");
        var hash = Files[sourcePath];
        Files.Remove(sourcePath);
        Files[destinationPath] = hash;
    }

    public void DeleteFile(string path)
    {
        Operations.Add("DeleteFile");
        Files.Remove(Path.GetFullPath(path));
    }

    public string? ReadRegistryValue(string name) => Registry.GetValueOrDefault(name);

    public void SetRegistryValue(string name, string path)
    {
        Operations.Add("SetRegistry");
        Registry[name] = path;
    }

    public void DeleteRegistryValueIfMatching(string name, string path)
    {
        Operations.Add("DeleteRegistry");
        if (Registry.TryGetValue(name, out var value) &&
            Path.GetFullPath(value).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            Registry.Remove(name);
    }

    public ManagedFontEntry? FindManagedFont(string path) => Managed.GetValueOrDefault(Path.GetFullPath(path));

    public void AddManagedFont(ManagedFontEntry entry)
    {
        Operations.Add("AddManaged");
        Managed[Path.GetFullPath(entry.InstalledPath)] = entry;
    }

    public void RemoveManagedFont(string path)
    {
        Operations.Add("RemoveManaged");
        Managed.Remove(Path.GetFullPath(path));
    }

    public void TryRemoveFontResource(string path) => Operations.Add("RemoveResource");
    public void AddFontResource(string path) => Operations.Add("AddResource");
    public void BroadcastFontChange() => Operations.Add("Broadcast");
}
