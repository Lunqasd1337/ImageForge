using System.Globalization;

namespace ImageForge.App;

/// <summary>Writes unhandled UI exceptions to %LOCALAPPDATA%\ImageForge\logs\app-crash.log, so a startup failure is not silent.</summary>
internal static class CrashLog
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageForge", "logs", "app-crash.log");

    public static void Write(string source, object? exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:O} {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}"));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
