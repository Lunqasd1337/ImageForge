namespace ImageForge.Core.Appx;

public static class AppxWarnings
{
    /// <summary>A warning to show before removing the package, or null when removal has no known side effect.</summary>
    public static string? For(PackageIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return identity.FamilyName switch
        {
            "Microsoft.WindowsStore_8wekyb3d8bbwe" =>
                Messages.AppxStoreWarning,
            "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe" =>
                Messages.AppxInstallerWarning,
            _ => null,
        };
    }
}
