namespace ImageForge.Core.Safety;

public static class OfflineHivePaths
{
    /// <summary>Joins a mount-relative hive file to the mount root and refuses anything outside the mount.</summary>
    public static string Resolve(WriteScope mount, string hiveFile)
    {
        ArgumentNullException.ThrowIfNull(mount);
        var check = mount.Check(mount.Root + "\\" + hiveFile);
        return check.IsAllowed
            ? check.NormalizedPath!
            : throw new UnauthorizedAccessException($"Hive file '{hiveFile}' is outside the mount ({check.Violation}).");
    }
}
