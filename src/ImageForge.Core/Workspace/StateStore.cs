using System.Text.Json;

namespace ImageForge.Core.Workspace;

public enum StateSource
{
    /// <summary>No state was ever saved.</summary>
    None,
    Current,

    /// <summary>The current file was damaged; the copy from the save before it was used.</summary>
    Previous,

    /// <summary>Both copies are damaged; the workspace needs recovery.</summary>
    Unusable,
}

public sealed record StateLoadResult<T>(T? State, StateSource Source);

/// <summary>Stores a JSON state file with atomic replacement and keeps the previous version as <c>.bak</c>.</summary>
public sealed class StateStore<T>(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Path { get; } = path;

    private string BackupPath => Path + ".bak";

    private string TempPath => Path + ".tmp";

    public void Save(T state)
    {
        using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state, Json);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(Path))
        {
            File.Replace(TempPath, Path, BackupPath);
        }
        else
        {
            File.Move(TempPath, Path);
        }
    }

    public StateLoadResult<T> Load()
    {
        var currentExists = File.Exists(Path);
        var backupExists = File.Exists(BackupPath);
        if (!currentExists && !backupExists)
        {
            return new StateLoadResult<T>(default, StateSource.None);
        }

        if (currentExists && TryRead(Path, out var current))
        {
            return new StateLoadResult<T>(current, StateSource.Current);
        }

        if (backupExists && TryRead(BackupPath, out var previous))
        {
            return new StateLoadResult<T>(previous, StateSource.Previous);
        }

        return new StateLoadResult<T>(default, StateSource.Unusable);
    }

    private static bool TryRead(string file, out T? state)
    {
        try
        {
            using var stream = File.OpenRead(file);
            state = JsonSerializer.Deserialize<T>(stream, Json);
            return state is not null;
        }
        catch (JsonException)
        {
            state = default;
            return false;
        }
    }
}
