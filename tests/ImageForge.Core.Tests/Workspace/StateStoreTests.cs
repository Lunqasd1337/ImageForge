using ImageForge.Core.Workspace;

namespace ImageForge.Core.Tests.Workspace;

public sealed class StateStoreTests
{
    private sealed record Sample(int Revision, string Name);

    [Fact]
    public void Saves_and_loads_the_latest_state()
    {
        using var dir = new TempDirectory();
        var store = new StateStore<Sample>(dir.Combine("state.json"));

        store.Save(new Sample(1, "a"));
        store.Save(new Sample(2, "b"));

        var loaded = store.Load();
        loaded.State.ShouldBe(new Sample(2, "b"));
        loaded.Source.ShouldBe(StateSource.Current);
    }

    [Fact]
    public void Falls_back_to_the_previous_good_state_when_the_current_file_is_damaged()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("state.json");
        var store = new StateStore<Sample>(path);
        store.Save(new Sample(1, "a"));
        store.Save(new Sample(2, "b"));

        File.WriteAllText(path, "{ broken");

        var loaded = store.Load();
        loaded.State.ShouldBe(new Sample(1, "a"));
        loaded.Source.ShouldBe(StateSource.Previous);
    }

    [Fact]
    public void Reports_missing_state()
    {
        using var dir = new TempDirectory();

        var loaded = new StateStore<Sample>(dir.Combine("state.json")).Load();

        loaded.State.ShouldBeNull();
        loaded.Source.ShouldBe(StateSource.None);
    }

    [Fact]
    public void Reports_unusable_state_when_both_copies_are_damaged()
    {
        using var dir = new TempDirectory();
        var path = dir.Combine("state.json");
        File.WriteAllText(path, "x");
        File.WriteAllText(path + ".bak", "y");

        new StateStore<Sample>(path).Load().Source.ShouldBe(StateSource.Unusable);
    }

    [Fact]
    public void Leaves_no_temporary_file_behind()
    {
        using var dir = new TempDirectory();
        var store = new StateStore<Sample>(dir.Combine("state.json"));

        store.Save(new Sample(1, "a"));
        store.Save(new Sample(2, "b"));

        Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ShouldBe(["state.json", "state.json.bak"]);
    }
}
