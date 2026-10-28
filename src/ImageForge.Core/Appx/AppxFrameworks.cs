namespace ImageForge.Core.Appx;

/// <summary>
/// Framework packages other apps depend on. Dependencies are not read from package manifests yet, so removing one is
/// blocked rather than risking apps that stay in the image (plan, section 8).
/// </summary>
public static class AppxFrameworks
{
    private static readonly string[] Prefixes =
    [
        "Microsoft.VCLibs.",
        "Microsoft.UI.Xaml.",
        "Microsoft.NET.Native.",
        "Microsoft.WindowsAppRuntime.",
        "Microsoft.Services.Store.Engagement",
    ];

    public static bool IsFramework(PackageIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return identity.PublisherId == "8wekyb3d8bbwe"
            && Prefixes.Any(p => identity.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
