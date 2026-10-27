using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageForge.Core.Workspace;

public enum JournalEventKind
{
    StepStarted,
    StepCompleted,
    StepFailed,
}

public sealed record JournalEntry(
    [property: JsonPropertyName("seq")] long Sequence,
    [property: JsonPropertyName("time")] DateTimeOffset TimeUtc,
    [property: JsonPropertyName("run")] Guid RunId,
    [property: JsonPropertyName("kind")] JournalEventKind Kind,
    [property: JsonPropertyName("step")] string Step,
    [property: JsonPropertyName("detail")] string? Detail);

/// <param name="IsCorrupt">A line could not be read or sequence numbers are not 1, 2, 3…; the run must go to recovery.</param>
/// <param name="LastOpenStep">A step that started and never completed or failed.</param>
public sealed record JournalReadResult(IReadOnlyList<JournalEntry> Entries, bool IsCorrupt, string? LastOpenStep);

/// <summary>Append-only JSON lines journal of a run. Each entry is flushed to disk before <see cref="Append"/> returns.</summary>
public sealed class RunJournal : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly FileStream _stream;
    private readonly Guid _runId;
    private long _sequence;

    private RunJournal(FileStream stream, Guid runId, long sequence)
    {
        _stream = stream;
        _runId = runId;
        _sequence = sequence;
    }

    public static RunJournal Open(string path, Guid runId)
    {
        var existing = Read(path);
        if (existing.IsCorrupt)
        {
            throw new InvalidDataException($"Journal '{path}' is damaged; the run needs recovery.");
        }

        if (existing.Entries.Any(e => e.RunId != runId))
        {
            throw new InvalidDataException($"Journal '{path}' belongs to another run.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new RunJournal(stream, runId, existing.Entries.Count);
    }

    public static JournalReadResult Read(string path)
    {
        if (!File.Exists(path))
        {
            return new JournalReadResult([], false, null);
        }

        var entries = new List<JournalEntry>();
        string? openStep = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            JournalEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<JournalEntry>(line, Json);
            }
            catch (JsonException)
            {
                entry = null;
            }

            if (entry is null || entry.Sequence != entries.Count + 1 || string.IsNullOrEmpty(entry.Step))
            {
                return new JournalReadResult(entries, true, openStep);
            }

            entries.Add(entry);
            openStep = entry.Kind == JournalEventKind.StepStarted ? entry.Step
                : string.Equals(openStep, entry.Step, StringComparison.Ordinal) ? null
                : openStep;
        }

        return new JournalReadResult(entries, false, openStep);
    }

    public void Append(JournalEventKind kind, string step, string? detail)
    {
        ArgumentException.ThrowIfNullOrEmpty(step);
        var entry = new JournalEntry(_sequence + 1, DateTimeOffset.UtcNow, _runId, kind, step, detail);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
        _stream.Write(bytes);
        _stream.Flush(flushToDisk: true);
        _sequence++;
    }

    public void Dispose() => _stream.Dispose();
}
