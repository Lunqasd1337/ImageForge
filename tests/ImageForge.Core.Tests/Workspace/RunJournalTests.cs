using ImageForge.Core.Workspace;

namespace ImageForge.Core.Tests.Workspace;

public sealed class RunJournalTests
{
    private static readonly Guid RunId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Entries_are_appended_with_consecutive_sequence_numbers_and_read_back()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("journal", "run.jsonl");

        using (var journal = RunJournal.Open(path, RunId))
        {
            journal.Append(JournalEventKind.StepStarted, "mount", "index 1");
            journal.Append(JournalEventKind.StepCompleted, "mount", null);
        }

        using (var reopened = RunJournal.Open(path, RunId))
        {
            reopened.Append(JournalEventKind.StepStarted, "registry", null);
        }

        var read = RunJournal.Read(path);
        read.IsCorrupt.ShouldBeFalse();
        read.Entries.Select(e => (e.Sequence, e.Kind, e.Step)).ShouldBe(
        [
            (1L, JournalEventKind.StepStarted, "mount"),
            (2L, JournalEventKind.StepCompleted, "mount"),
            (3L, JournalEventKind.StepStarted, "registry"),
        ]);
        read.Entries.ShouldAllBe(e => e.RunId == RunId);
        read.LastOpenStep.ShouldBe("registry");
    }

    [Fact]
    public void A_truncated_last_line_marks_the_journal_corrupt()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("run.jsonl");
        using (var journal = RunJournal.Open(path, RunId))
        {
            journal.Append(JournalEventKind.StepStarted, "mount", null);
        }

        File.AppendAllText(path, "{\"seq\":2,\"kind\":\"Step");

        var read = RunJournal.Read(path);
        read.IsCorrupt.ShouldBeTrue();
        read.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public void A_gap_in_sequence_numbers_marks_the_journal_corrupt()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("run.jsonl");
        using (var journal = RunJournal.Open(path, RunId))
        {
            journal.Append(JournalEventKind.StepStarted, "mount", null);
            journal.Append(JournalEventKind.StepCompleted, "mount", null);
        }

        var lines = File.ReadAllLines(path);
        File.WriteAllLines(path, [lines[1]]);

        RunJournal.Read(path).IsCorrupt.ShouldBeTrue();
    }

    [Fact]
    public void Opening_a_corrupt_journal_for_append_is_refused()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("run.jsonl");
        File.WriteAllText(path, "not json\n");

        Should.Throw<InvalidDataException>(() => RunJournal.Open(path, RunId));
    }

    [Fact]
    public void Completed_steps_leave_no_open_step()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("run.jsonl");
        using (var journal = RunJournal.Open(path, RunId))
        {
            journal.Append(JournalEventKind.StepStarted, "mount", null);
            journal.Append(JournalEventKind.StepFailed, "mount", "0x80070057");
        }

        RunJournal.Read(path).LastOpenStep.ShouldBeNull();
    }

    [Fact]
    public void Progress_entries_do_not_close_the_open_step()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("run.jsonl");
        using (var journal = RunJournal.Open(path, RunId))
        {
            journal.Append(JournalEventKind.StepStarted, "publish", null);
            journal.Append(JournalEventKind.StepProgress, "publish", "previous-moved");
        }

        RunJournal.Read(path).LastOpenStep.ShouldBe("publish");
    }
}
