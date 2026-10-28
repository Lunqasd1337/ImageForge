using ImageForge.Core.Workspace;

namespace ImageForge.Core.Tests.Workspace;

public sealed class ImageInspectionTests : IDisposable
{
    private const string Clipchamp = "Clipchamp.Clipchamp_4.3.10120.0_neutral_~_yxz26nhyzhsrt";
    private readonly TempDirectory _dir = new();
    private readonly FakeServicing _servicing = new();
    private readonly TestPaths _paths;
    private readonly string _source;

    public ImageInspectionTests()
    {
        _paths = new TestPaths(_dir.Combine("work"));
        _source = _dir.Combine("media");
        _dir.WriteFile("media/setup.exe", "setup");
        _dir.WriteFile("media/sources/install.wim", "WIM");
        _servicing.Provisioned.Add(Clipchamp);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private ImageInspection Inspection(List<string>? log = null) => new(_paths, _servicing) { Log = log is null ? null : log.Add };

    [Fact]
    public async Task Reads_the_packages_from_a_read_only_mount_of_the_verified_copy()
    {
        var inventory = await Inspection().InspectAsync(_source, Guid.NewGuid(), 1, Ct);

        inventory.Packages.ShouldHaveSingleItem().PackageName.ShouldBe(Clipchamp);
        _servicing.Calls.ShouldBe(["mount read-only", "appx-list", "unmount discard"]);
        File.ReadAllText(Path.Combine(_paths.Base, "sources", "install.wim")).ShouldBe("WIM");
        Directory.Exists(_paths.InspectMount).ShouldBeFalse();
        new StateStore<WorkspaceState>(_paths.StateFile).Load().State!.SourceFingerprint.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_later_inspection_reuses_the_verified_copy()
    {
        var workspace = Guid.NewGuid();
        await Inspection().InspectAsync(_source, workspace, 1, Ct);
        var log = new List<string>();

        await Inspection(log).InspectAsync(_source, workspace, 1, Ct);

        log.ShouldContain("base copy reused");
    }

    [Fact]
    public async Task A_failed_list_still_discards_the_mount()
    {
        _servicing.FailAppxList = true;

        await Should.ThrowAsync<IOException>(() => Inspection().InspectAsync(_source, Guid.NewGuid(), 1, Ct));

        _servicing.Mounted.ShouldBeEmpty();
        _servicing.Calls.ShouldContain("unmount discard");
        Directory.Exists(_paths.InspectMount).ShouldBeFalse();
    }

    [Fact]
    public async Task An_inspection_mount_left_behind_is_discarded_by_recovery()
    {
        Directory.CreateDirectory(_paths.InspectMount);
        await _servicing.MountReadOnlyAsync("x.wim", 1, _paths.InspectMount, Ct);
        ImageInspection.LeftBehind(_paths).ShouldBeTrue();

        var result = await new RunRecovery(_paths, _servicing).RecoverAsync(Ct);

        result.Outcome.ShouldBe(RecoveryOutcome.Recovered, string.Join("; ", result.Problems));
        _servicing.Mounted.ShouldBeEmpty();
        ImageInspection.LeftBehind(_paths).ShouldBeFalse();
    }

    [Fact]
    public async Task Inspection_waits_for_recovery_after_an_interrupted_run()
    {
        Directory.CreateDirectory(_paths.InspectMount);
        File.WriteAllText(Path.Combine(_paths.InspectMount, "marker.txt"), "left");

        await Should.ThrowAsync<WorkspaceRecoveryRequiredException>(() => Inspection().InspectAsync(_source, Guid.NewGuid(), 1, Ct));
        _servicing.Calls.ShouldBeEmpty();
    }
}
