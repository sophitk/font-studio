using System.Text.Json;
using Microsoft.Win32;

namespace FontStudio.Services;

public sealed record PilotPolicy(bool AllowFontChanges, string Mode)
{
    private const string DisposableVmMode = "Disposable VM mutation test only";
    private const string DisposableHyperVMode = "Disposable Hyper-V VM mutation test only";
    private const string CurrentUserMode = "Current-user font management";

    public bool RequiresSessionConfirmation => Mode.Equals(CurrentUserMode, StringComparison.Ordinal);

    public static PilotPolicy Load()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "pilot-policy.json");
            if (!File.Exists(path)) return ReadOnly();
            var policy = JsonSerializer.Deserialize<PilotPolicy>(File.ReadAllText(path));
            return policy ?? ReadOnly();
        }
        catch
        {
            return ReadOnly();
        }
    }

    private static PilotPolicy ReadOnly() => new(false, "Read-only pilot");

    public void RequireFontChangesEnabled()
    {
        if (!AllowFontChanges)
            throw new InvalidOperationException("Font changes are disabled by the current pilot policy.");
        var allowedBoundary = Mode switch
        {
            DisposableVmMode => IsConfiguredWindowsSandbox(),
            DisposableHyperVMode => IsConfiguredHyperVGuest(),
#if FONTSTUDIO_CURRENT_USER_MUTATION
            CurrentUserMode => IsConfiguredCurrentUserPackage(),
#endif
            _ => false
        };
        if (!allowedBoundary)
            throw new InvalidOperationException("Font changes are not enabled for this FontStudio package.");
    }

    private static bool IsConfiguredWindowsSandbox()
    {
        var packageRoot = Path.GetFullPath(@"C:\FontStudioMutation") + Path.DirectorySeparatorChar;
        return Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFullPath(AppContext.BaseDirectory).StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConfiguredHyperVGuest()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var packageRoot = Path.GetFullPath(Path.Combine(downloads, "FontStudioHyperVPilot")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(AppContext.BaseDirectory).StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", false);
        var manufacturer = bios?.GetValue("SystemManufacturer") as string;
        var productName = bios?.GetValue("SystemProductName") as string;
        return manufacturer?.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) == true &&
            productName?.Contains("Virtual Machine", StringComparison.OrdinalIgnoreCase) == true;
    }

#if FONTSTUDIO_CURRENT_USER_MUTATION
    private static bool IsConfiguredCurrentUserPackage()
    {
        var acknowledgement = Path.Combine(AppContext.BaseDirectory, "CURRENT-USER-FONT-MANAGEMENT.txt");
        return File.Exists(acknowledgement) &&
            File.ReadAllText(acknowledgement).Contains("CURRENT-USER FONT MANAGEMENT", StringComparison.Ordinal);
    }
#endif

}
