using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;

namespace FontStudio.Services;

public enum FontMutationStage
{
    AfterFileCopy,
    AfterRegistryWrite,
    AfterFontResourceAdd,
    AfterManifestWrite,
    BeforeInstallBroadcast,
    AfterFontResourceRemove,
    AfterQuarantineMove,
    AfterRegistryDelete,
    AfterManifestRemove,
    BeforeDeactivateBroadcast
}

public sealed class FontActivationService
{
    private const string FontRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
    private const uint FontResourceFlags = 0;
    private const int HwndBroadcast = 0xffff;
    private const int WmFontChange = 0x001D;
    private const uint SmtoAbortIfHung = 0x0002;
    private readonly string _userFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
    private readonly ManagedFontStore _managedFonts = new();
    private readonly AuditService _audit = new();
    private readonly PilotPolicy _policy = PilotPolicy.Load();
    private readonly FontMutationJournalService _journal;
    private readonly Action<FontMutationStage>? _stageHook;

    public FontActivationService(
        Action<FontMutationStage>? stageHook = null,
        FontMutationJournalService? journal = null)
    {
        _stageHook = stageHook;
        _journal = journal ?? new FontMutationJournalService();
    }

    public string ActivateForCurrentUser(string sourcePath)
    {
        _policy.RequireFontChangesEnabled();
        var inspection = FontFileInspector.Inspect(sourcePath);
        if (!inspection.IsValid) throw new InvalidDataException(inspection.Message);

        Directory.CreateDirectory(_userFonts);
        var destination = GetAvailableDestination(Path.GetFileName(sourcePath));
        var registryName = $"{Path.GetFileNameWithoutExtension(destination)} (FontStudio {inspection.Format})";
        var managedEntry = new ManagedFontEntry(
            Path.GetFileName(sourcePath), destination, registryName, inspection.Sha256, DateTimeOffset.UtcNow);
        var transaction = _journal.BeginInstall(managedEntry);
        var fontResourceAdded = false;

        try
        {
            File.Copy(sourcePath, destination, false);
            transaction = _journal.Advance(transaction, FontMutationStep.FileCopied);
            _stageHook?.Invoke(FontMutationStage.AfterFileCopy);
            var copiedInspection = FontFileInspector.Inspect(destination);
            if (!copiedInspection.IsValid || !copiedInspection.Sha256.Equals(inspection.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The font changed while it was being copied. Installation was stopped.");

            using var key = Registry.CurrentUser.CreateSubKey(FontRegistryPath, true);
            key.SetValue(registryName, destination, RegistryValueKind.String);
            transaction = _journal.Advance(transaction, FontMutationStep.RegistryWritten);
            _stageHook?.Invoke(FontMutationStage.AfterRegistryWrite);
            if (AddFontResourceEx(destination, FontResourceFlags, IntPtr.Zero) == 0)
                throw new InvalidOperationException("Windows did not activate the font resource.");
            fontResourceAdded = true;
            transaction = _journal.Advance(transaction, FontMutationStep.ResourceChanged);
            _stageHook?.Invoke(FontMutationStage.AfterFontResourceAdd);
            _managedFonts.Add(managedEntry);
            transaction = _journal.Advance(transaction, FontMutationStep.ManifestChanged);
            _stageHook?.Invoke(FontMutationStage.AfterManifestWrite);
            _stageHook?.Invoke(FontMutationStage.BeforeInstallBroadcast);
            BroadcastFontChange();
            transaction = _journal.Advance(transaction, FontMutationStep.BroadcastCompleted);
            _audit.Write("Install", destination, "Success", $"SHA-256 {inspection.Sha256}");
            _journal.Clear(transaction.TransactionId);
            return destination;
        }
        catch (Exception ex)
        {
            var rollbackIssues = new List<string>();
            if (fontResourceAdded)
            {
                TryRollback(() =>
                {
                    if (!RemoveFontResourceEx(destination, FontResourceFlags, IntPtr.Zero))
                        throw new InvalidOperationException("Windows did not release the activated font resource.");
                }, "font resource", rollbackIssues);
            }
            TryRollback(() =>
            {
                using var rollbackKey = Registry.CurrentUser.OpenSubKey(FontRegistryPath, true);
                rollbackKey?.DeleteValue(registryName, false);
            }, "registry entry", rollbackIssues);
            TryRollback(() => { if (File.Exists(destination)) File.Delete(destination); }, "font file", rollbackIssues);
            TryRollback(() => _managedFonts.Remove(destination), "manifest", rollbackIssues);
            if (rollbackIssues.Count == 0)
                TryRollback(() => _journal.Clear(transaction.TransactionId), "mutation journal", rollbackIssues);
            var details = rollbackIssues.Count == 0 ? ex.Message : $"{ex.Message} Rollback issues: {string.Join(", ", rollbackIssues)}";
            _audit.Write("Install", sourcePath, "Failed", details);
            if (rollbackIssues.Count > 0) throw new InvalidOperationException(details, ex);
            throw;
        }
    }

    public void DeactivateCurrentUserFont(string sourcePath)
    {
        _policy.RequireFontChangesEnabled();
        var fullPath = Path.GetFullPath(sourcePath);
        var allowedRoot = Path.GetFullPath(_userFonts) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("FontStudio only deactivates fonts installed for the current user.");

        var manifestEntry = _managedFonts.FindByPath(fullPath)
            ?? throw new InvalidOperationException("This font is not present in the FontStudio installation manifest.");
        var inspection = FontFileInspector.Inspect(fullPath);
        if (!inspection.IsValid || !inspection.Sha256.Equals(manifestEntry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The installed file changed after installation. FontStudio stopped to prevent removing the wrong file.");

        var managedRegistryName = string.Empty;
        using var key = Registry.CurrentUser.OpenSubKey(FontRegistryPath, true);
        if (key is not null)
        {
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is string value &&
                    Path.GetFullPath(value).Equals(fullPath, StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("(FontStudio ", StringComparison.OrdinalIgnoreCase))
                {
                    managedRegistryName = name;
                    break;
                }
            }
        }
        if (string.IsNullOrEmpty(managedRegistryName))
            throw new InvalidOperationException("This font was not installed by FontStudio and will not be removed.");

        var quarantinePath = fullPath + $".fontstudio-removing-{Guid.NewGuid():N}";
        var transaction = _journal.BeginDeactivate(manifestEntry, quarantinePath);
        var removedResourceCount = 0;
        try
        {
            removedResourceCount = RemoveFontResources(fullPath);
            transaction = _journal.Advance(transaction, FontMutationStep.ResourceChanged);
            _stageHook?.Invoke(FontMutationStage.AfterFontResourceRemove);
            File.Move(fullPath, quarantinePath, false);
            transaction = _journal.Advance(transaction, FontMutationStep.FileQuarantined);
            _stageHook?.Invoke(FontMutationStage.AfterQuarantineMove);
            key?.DeleteValue(managedRegistryName, false);
            transaction = _journal.Advance(transaction, FontMutationStep.RegistryRemoved);
            _stageHook?.Invoke(FontMutationStage.AfterRegistryDelete);
            _managedFonts.Remove(fullPath);
            transaction = _journal.Advance(transaction, FontMutationStep.ManifestChanged);
            _stageHook?.Invoke(FontMutationStage.AfterManifestRemove);
            _stageHook?.Invoke(FontMutationStage.BeforeDeactivateBroadcast);
            BroadcastFontChange();
            transaction = _journal.Advance(transaction, FontMutationStep.BroadcastCompleted);
            File.Delete(quarantinePath);
            transaction = _journal.Advance(transaction, FontMutationStep.QuarantineDeleted);
        }
        catch (Exception ex)
        {
            var rollbackIssues = new List<string>();
            TryRollback(() => { if (File.Exists(quarantinePath) && !File.Exists(fullPath)) File.Move(quarantinePath, fullPath, false); }, "font file", rollbackIssues);
            TryRollback(() =>
            {
                using var restoreKey = Registry.CurrentUser.CreateSubKey(FontRegistryPath, true);
                restoreKey.SetValue(manifestEntry.RegistryName, fullPath, RegistryValueKind.String);
            }, "registry entry", rollbackIssues);
            TryRollback(() => _managedFonts.Add(manifestEntry), "manifest", rollbackIssues);
            TryRollback(() =>
            {
                for (var index = 0; index < removedResourceCount; index++)
                {
                    if (AddFontResourceEx(fullPath, FontResourceFlags, IntPtr.Zero) == 0)
                        throw new InvalidOperationException("Windows did not restore the font resource.");
                }
            }, "font resource", rollbackIssues);
            TryRollback(BroadcastFontChange, "font cache notification", rollbackIssues);
            if (rollbackIssues.Count == 0)
                TryRollback(() => _journal.Clear(transaction.TransactionId), "mutation journal", rollbackIssues);
            var details = rollbackIssues.Count == 0 ? ex.Message : $"{ex.Message} Rollback issues: {string.Join(", ", rollbackIssues)}";
            _audit.Write("Deactivate", fullPath, "Failed", details);
            if (rollbackIssues.Count > 0) throw new InvalidOperationException(details, ex);
            throw;
        }
        _audit.Write("Deactivate", fullPath, "Success", $"SHA-256 {inspection.Sha256}");
        _journal.Clear(transaction.TransactionId);
    }

    private string GetAvailableDestination(string fileName)
    {
        var destination = Path.Combine(_userFonts, fileName);
        if (!File.Exists(destination)) return destination;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return Path.Combine(_userFonts, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    private static void TryRollback(Action action, string label, ICollection<string> issues)
    {
        try { action(); }
        catch (Exception rollbackException) { issues.Add($"{label}: {rollbackException.Message}"); }
    }

    private static int RemoveFontResources(string path)
    {
        if (!RemoveFontResourceEx(path, FontResourceFlags, IntPtr.Zero))
            throw new InvalidOperationException("Windows did not release FontStudio's font resource; no files were changed.");
        return 1;
    }

    private static void BroadcastFontChange()
    {
        var result = SendMessageTimeout((IntPtr)HwndBroadcast, WmFontChange, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, 2000, out _);
        if (result == IntPtr.Zero) throw new InvalidOperationException("Windows did not acknowledge the font-cache notification within 2 seconds.");
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceEx(string name, uint flags, IntPtr reserved);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool RemoveFontResourceEx(string name, uint flags, IntPtr reserved);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
