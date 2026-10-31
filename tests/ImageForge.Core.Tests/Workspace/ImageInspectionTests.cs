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

        inventory.Appx.Packages.ShouldHaveSingleItem().PackageName.ShouldBe(Clipchamp);
        _servicing.Calls.ShouldBe(["mount read-only", "appx-list", "feature-list", "feature-parents", "capability-list", "capability-names", "driver-list", "package-list", "unmount discard"]);
        File.ReadAllText(Path.Combine(_paths.Base, "sources", "install.wim")).ShouldBe("WIM");
        Directory.Exists(_paths.InspectMount).ShouldBeFalse();
        new StateStore<WorkspaceState>(_paths.StateFile).Load().State!.SourceFingerprint.ShouldNotBeNull();
    }

    [Fact]
    public async Task Without_space_for_the_base_copy_nothing_is_copied()
    {
        var inspection = new ImageInspection(_paths, _servicing) { FreeSpace = _ => 1024 };

        (await Should.ThrowAsync<IOException>(() => inspection.InspectAsync(_source, Guid.NewGuid(), 1, Ct))).Message.ShouldContain("Not enough space");
        Directory.Exists(_paths.Base).ShouldBeFalse();
        _servicing.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reads_features_with_parents_capabilities_and_the_netfx3_source()
    {
        _servicing.Features["NetFx3"] = "Disabled with Payload Removed";
        _servicing.Features["WCF-HTTP-Activation"] = "Disabled";
        _servicing.FeatureParents["NetFx3"] = [];
        _servicing.FeatureParents["WCF-HTTP-Activation"] = ["NetFx3"];
        _servicing.Capabilities["MathRecognizer~~~~0.0.1.0"] = "Installed";
        _dir.WriteFile("media/sources/sxs/microsoft-windows-netfx3-ondemand-package~31bf3856ad364e35~amd64~~.cab", "CAB");

        var inventory = await Inspection().InspectAsync(_source, Guid.NewGuid(), 1, Ct);

        inventory.Features.Features.Select(f => f.Name).ShouldBe(["NetFx3", "WCF-HTTP-Activation"]);
        inventory.Features.Parents!["WCF-HTTP-Activation"].ShouldBe(["NetFx3"]);
        inventory.Capabilities.Capabilities.ShouldHaveSingleItem().State.ShouldBe(ImageForge.Core.Features.CapabilityState.Installed);
        inventory.MediaHasNetFx3.ShouldBeTrue();
    }

    [Fact]
    public async Task Media_without_the_netfx3_package_is_reported()
    {
        _dir.WriteFile("media/sources/sxs/readme.txt", "none");

        (await Inspection().InspectAsync(_source, Guid.NewGuid(), 1, Ct)).MediaHasNetFx3.ShouldBeFalse();
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
