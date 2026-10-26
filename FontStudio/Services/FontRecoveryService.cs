using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FontStudio.Services;

public sealed record FontRecoveryResult(
    bool HadPendingMutation,
    Guid? TransactionId,
    string Outcome,
    string Details);

public interface IFontRecoveryPlatform
{
    bool FileExists(string path);
    string FileSha256(string path);
    void MoveFile(string source, string destination);
    void DeleteFile(string path);
    string? ReadRegistryValue(string name);
    void SetRegistryValue(string name, string path);
    void DeleteRegistryValueIfMatching(string name, string path);
    ManagedFontEntry? FindManagedFont(string path);
    void AddManagedFont(ManagedFontEntry entry);
    void RemoveManagedFont(string path);
    void TryRemoveFontResource(string path);
    void AddFontResource(string path);
    void BroadcastFontChange();
}

public sealed class FontRecoveryService
{
    private readonly FontMutationJournalService _journal;
    private readonly IFontRecoveryPlatform _platform;
    private readonly AuditService _audit;

    public FontRecoveryService(
        FontMutationJournalService? journal = null,
        IFontRecoveryPlatform? platform = null,
        AuditService? audit = null)
    {
        _journal = journal ?? new FontMutationJournalService();
        _platform = platform ?? new WindowsFontRecoveryPlatform();
        _audit = audit ?? new AuditService();
    }

    public FontRecoveryResult RecoverPending()
    {
        using var recoveryMutex = new Mutex(false, @"Local\FontStudio-Mutation-Recovery-v1");
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = recoveryMutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }
            if (!ownsMutex)
                throw new InvalidOperationException(
                    "Another FontStudio process is already reconciling a pending mutation.");

            return RecoverPendingCore();
        }
        finally
        {
            if (ownsMutex) recoveryMutex.ReleaseMutex();
        }
    }

    private FontRecoveryResult RecoverPendingCore()
    {
        var pending = _journal.LoadPending();
        if (pending is null)
            return new FontRecoveryResult(false, null, "NoAction", "No pending font mutation was found.");

        try
        {
            var outcome = pending.Kind switch
            {
                FontMutationKind.Install => RollBackInstall(pending),
                FontMutationKind.Deactivate => ReconcileDeactivate(pending),
                _ => throw new InvalidDataException("The pending mutation type is not supported.")
            };
            _journal.Clear(pending.TransactionId);
            _audit.Write("Recovery", pending.InstalledPath, "Success",
                $"Transaction {pending.TransactionId}; {outcome}");
            return new FontRecoveryResult(true, pending.TransactionId, "Recovered", outcome);
        }
        catch (Exception ex)
        {
            _audit.Write("Recovery", pending.InstalledPath, "Failed",
                $"Transaction {pending.TransactionId}; {ex.Message}");
            throw new InvalidOperationException(
                $"Recovery for transaction {pending.TransactionId} could not finish. Mutation remains locked. {ex.Message}", ex);
        }
    }

    private string RollBackInstall(FontMutationTransaction transaction)
    {
        if (_platform.FileExists(transaction.InstalledPath))
            RequireExpectedHash(transaction.InstalledPath, transaction.Sha256);

        _platform.TryRemoveFontResource(transaction.InstalledPath);
        _platform.DeleteRegistryValueIfMatching(transaction.RegistryName, transaction.InstalledPath);
        _platform.RemoveManagedFont(transaction.InstalledPath);
        if (_platform.FileExists(transaction.InstalledPath))
            _platform.DeleteFile(transaction.InstalledPath);
        _platform.BroadcastFontChange();
        return "Incomplete install rolled back to the pre-install state.";
    }

    private string ReconcileDeactivate(FontMutationTransaction transaction)
    {
        var installedExists = _platform.FileExists(transaction.InstalledPath);
        var quarantineExists = transaction.QuarantinePath is not null &&
            _platform.FileExists(transaction.QuarantinePath);

        if (installedExists)
        {
            RequireExpectedHash(transaction.InstalledPath, transaction.Sha256);
            if (quarantineExists)
            {
                RequireExpectedHash(transaction.QuarantinePath!, transaction.Sha256);
                _platform.DeleteFile(transaction.QuarantinePath!);
            }
            RestoreInstalledState(transaction);
            return "Interrupted deactivation rolled back to an installed state.";
        }

        if (quarantineExists)
        {
            RequireExpectedHash(transaction.QuarantinePath!, transaction.Sha256);
            _platform.MoveFile(transaction.QuarantinePath!, transaction.InstalledPath);
            RestoreInstalledState(transaction);
            return "Quarantined font restored and deactivation rolled back.";
        }

        _platform.DeleteRegistryValueIfMatching(transaction.RegistryName, transaction.InstalledPath);
        _platform.RemoveManagedFont(transaction.InstalledPath);
        _platform.BroadcastFontChange();
        return "Deactivation had already removed the font file and was completed forward.";
    }

    private void RestoreInstalledState(FontMutationTransaction transaction)
    {
        var registryValue = _platform.ReadRegistryValue(transaction.RegistryName);
        if (registryValue is not null &&
            !PathsEqual(registryValue, transaction.InstalledPath))
            throw new InvalidOperationException(
                "The recorded registry name now points to a different path; it was not overwritten.");

        _platform.SetRegistryValue(transaction.RegistryName, transaction.InstalledPath);
        _platform.AddManagedFont(new ManagedFontEntry(
            transaction.SourceName,
            transaction.InstalledPath,
            transaction.RegistryName,
            transaction.Sha256,
            transaction.InstalledAtUtc));
        _platform.AddFontResource(transaction.InstalledPath);
        _platform.BroadcastFontChange();
    }

    private void RequireExpectedHash(string path, string expected)
    {
        var actual = _platform.FileSha256(path);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Recovery found an unexpected file hash for {Path.GetFileName(path)}; no destructive file action was taken.");
    }

    private static bool PathsEqual(string left, string right)
    {
        if (!Path.IsPathRooted(left)) return false;
        return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class WindowsFontRecoveryPlatform : IFontRecoveryPlatform
{
    private const string FontRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
    private const uint FontResourceFlags = 0;
    private const int HwndBroadcast = 0xffff;
    private const int WmFontChange = 0x001D;
    private const uint SmtoAbortIfHung = 0x0002;
    private readonly ManagedFontStore _managedFonts;

    public WindowsFontRecoveryPlatform(ManagedFontStore? managedFonts = null)
    {
        _managedFonts = managedFonts ?? new ManagedFontStore();
    }

    public bool FileExists(string path) => File.Exists(path);

    public string FileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public void MoveFile(string source, string destination) => File.Move(source, destination, false);

    public void DeleteFile(string path) => File.Delete(path);

    public string? ReadRegistryValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(FontRegistryPath, false);
        return key?.GetValue(name) as string;
    }

    public void SetRegistryValue(string name, string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(FontRegistryPath, true);
        key.SetValue(name, path, RegistryValueKind.String);
    }

    public void DeleteRegistryValueIfMatching(string name, string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(FontRegistryPath, true);
        if (key?.GetValue(name) is not string value || !Path.IsPathRooted(value) ||
            !Path.GetFullPath(value).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return;
        key.DeleteValue(name, false);
    }

    public ManagedFontEntry? FindManagedFont(string path) => _managedFonts.FindByPath(path);

    public void AddManagedFont(ManagedFontEntry entry) => _managedFonts.Add(entry);

    public void RemoveManagedFont(string path) => _managedFonts.Remove(path);

    public void TryRemoveFontResource(string path) =>
        RemoveFontResourceEx(path, FontResourceFlags, IntPtr.Zero);

    public void AddFontResource(string path)
    {
        if (AddFontResourceEx(path, FontResourceFlags, IntPtr.Zero) == 0)
            throw new InvalidOperationException("Windows did not restore the recovered font resource.");
    }

    public void BroadcastFontChange()
    {
        var result = SendMessageTimeout((IntPtr)HwndBroadcast, WmFontChange, IntPtr.Zero,
            IntPtr.Zero, SmtoAbortIfHung, 2000, out _);
        if (result == IntPtr.Zero)
            throw new InvalidOperationException(
                "Windows did not acknowledge the recovery font-cache notification within 2 seconds.");
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceEx(string name, uint flags, IntPtr reserved);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool RemoveFontResourceEx(string name, uint flags, IntPtr reserved);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int message, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
