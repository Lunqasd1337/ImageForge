using ImageForge.Core.Workspace;

namespace ImageForge.Engine.Workspace;

/// <summary>What the engine reads about the user's folders for the plan's section 6 checks.</summary>
public static class LocalVolumes
{
    private static readonly string[] OneDriveVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    public static VolumeFacts? Facts(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            var kind = drive.DriveType switch
            {
                DriveType.Network => VolumeKind.Network,
                DriveType.Removable => VolumeKind.Removable,
                _ => VolumeKind.Local,
            };
            return new VolumeFacts(kind, drive.IsReady ? drive.DriveFormat : null);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Under a OneDrive folder of the user, or under a folder named like another sync client's. The elevated engine runs as
    /// the same user, so it sees the same OneDrive variables as the window.
    /// </summary>
    public static bool IsSynced(string path) => WorkspaceLocations.IsSynced(path,
        OneDriveVariables
            .Select(Environment.GetEnvironmentVariable)
            .OfType<string>()
            .Where(r => r.Length > 0));
}
