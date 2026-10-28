using ImageForge.Core.Appx;
using ImageForge.Core.Changes;
using ImageForge.Core.Execution;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;
using ImageForge.Core.Workspace;

namespace ImageForge.Core.Tests.Workspace;

public sealed class RunPipelineTests : IDisposable
{
    private static readonly Guid ImageId = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private readonly TempDirectory _dir = new();
    private readonly FakeServicing _servicing = new();
    private readonly TestPaths _paths;
    private readonly string _source;
    private readonly string _output;

    public RunPipelineTests()
    {
        _paths = new TestPaths(_dir.Combine("work"));
        _source = _dir.Combine("media");
        _output = _dir.Combine("out", "Win11");
        _dir.WriteFile("media/setup.exe", "setup");
        _dir.WriteFile("media/sources/install.wim", "WIM");
        _dir.WriteFile("media/sources/boot.wim", "BOOT");
        File.SetAttributes(_dir.Combine("media", "sources", "install.wim"), FileAttributes.ReadOnly);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir.Path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        _dir.Dispose();
    }

    [Fact]
    public async Task A_successful_run_publishes_the_serviced_media_and_releases_everything()
    {
        var sourceBefore = await SourceManifest.ComputeAsync(_source, Ct);

        var result = await Pipeline().RunAsync(Request(), Ct);

        result.Status.ShouldBe(RunStatus.Published, result.Error);
        result.PublishedPath.ShouldBe(_output);
        File.ReadAllText(Path.Combine(_output, "sources", "install.wim")).ShouldBe("WIM+M1+export");
        File.ReadAllText(Path.Combine(_output, "sources", "boot.wim")).ShouldBe("BOOT");
        File.ReadAllText(Path.Combine(_output, "setup.exe")).ShouldBe("setup");
        SourceManifest.Compare(sourceBefore, await SourceManifest.ComputeAsync(_source, Ct)).ShouldBeEmpty();
        _servicing.Mounted.ShouldBeEmpty();
        _servicing.Calls.ShouldBe(["mount", "registry", "unmount commit", "export"]);
        Directory.GetFileSystemEntries(_dir.Combine("out")).ShouldBe([_output]);
        Directory.Exists(_paths.Run(result.RunId).Root).ShouldBeFalse();
        var state = State();
        state.Target.ShouldBe(TargetStatus.Completed);
        state.LastRun!.Status.ShouldBe(RunStatus.Published);
        RunJournal.Read(_paths.Journal(result.RunId)).LastOpenStep.ShouldBeNull();
    }

    [Fact]
    public async Task A_second_run_replaces_the_previous_result_and_reuses_the_verified_copy()
    {
        var log = new List<string>();
        (await Pipeline(log).RunAsync(Request(), Ct)).Status.ShouldBe(RunStatus.Published);
        File.WriteAllText(Path.Combine(_output, "stale.txt"), "old");

        (await Pipeline(log).RunAsync(Request(), Ct)).Status.ShouldBe(RunStatus.Published);

        File.Exists(Path.Combine(_output, "stale.txt")).ShouldBeFalse();
        log.ShouldContain("base copy reused");
    }

    [Fact]
    public async Task Failure_before_publication_keeps_source_and_previous_result_and_marks_target_for_rebuild()
    {
        (await Pipeline().RunAsync(Request(), Ct)).Status.ShouldBe(RunStatus.Published);
        File.WriteAllText(Path.Combine(_output, "previous.txt"), "previous");
        var previous = await SourceManifest.ComputeAsync(_output, Ct);
        var sourceBefore = await SourceManifest.ComputeAsync(_source, Ct);

        var result = await Pipeline().RunAsync(Request(InjectedFailure.BeforePublish), Ct);

        result.Status.ShouldBe(RunStatus.Failed);
        result.FailedStep.ShouldBe("publish");
        result.PublishedPath.ShouldBeNull();
        SourceManifest.Compare(previous, await SourceManifest.ComputeAsync(_output, Ct)).ShouldBeEmpty();
        SourceManifest.Compare(sourceBefore, await SourceManifest.ComputeAsync(_source, Ct)).ShouldBeEmpty();
        Directory.GetFileSystemEntries(_dir.Combine("out")).ShouldBe([_output]);
        Directory.Exists(_paths.Run(result.RunId).Root).ShouldBeFalse();
        State().Target.ShouldBe(TargetStatus.NeedsRebuild);
        State().LastRun!.Status.ShouldBe(RunStatus.Failed);
    }

    [Fact]
    public async Task Failure_after_mount_discards_the_mount_without_committing()
    {
        var result = await Pipeline().RunAsync(Request(InjectedFailure.AfterMount), Ct);

        result.Status.ShouldBe(RunStatus.Failed);
        result.FailedStep.ShouldBe("registry");
        _servicing.Calls.ShouldBe(["mount", "unmount discard"]);
        _servicing.Mounted.ShouldBeEmpty();
        Directory.Exists(_output).ShouldBeFalse();
        Directory.Exists(_paths.Mount(ImageId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Cancellation_stops_at_the_next_step_and_discards_the_mount()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pipeline = Pipeline(beforeStep: step =>
        {
            if (step == "registry")
            {
                cts.Cancel();
            }
        });

        var result = await pipeline.RunAsync(Request(), cts.Token);

        result.Status.ShouldBe(RunStatus.Failed);
        result.FailedStep.ShouldBe("registry");
        _servicing.Calls.ShouldBe(["mount", "unmount discard"]);
        _servicing.Mounted.ShouldBeEmpty();
        State().Target.ShouldBe(TargetStatus.NeedsRebuild);
        Directory.Exists(_output).ShouldBeFalse();
    }

    [Fact]
    public async Task Failed_export_is_reported_with_its_step()
    {
        _servicing.FailExport = true;

        var result = await Pipeline().RunAsync(Request(), Ct);

        result.Status.ShouldBe(RunStatus.Failed);
        result.FailedStep.ShouldBe("export");
        result.Error.ShouldNotBeNull().ShouldContain("export failed");
        Directory.Exists(_output).ShouldBeFalse();
    }

    [Fact]
    public async Task A_hive_left_loaded_puts_the_workspace_into_recovery()
    {
        _servicing.LeaveHiveLoaded = true;

        var result = await Pipeline().RunAsync(Request(), Ct);

        result.Status.ShouldBe(RunStatus.RecoveryRequired);
        await Should.ThrowAsync<WorkspaceRecoveryRequiredException>(() => Pipeline().RunAsync(Request(), Ct));
    }

    [Fact]
    public async Task A_source_changed_during_the_run_is_not_published()
    {
        var pipeline = Pipeline(beforeStep: step =>
        {
            if (step == "source-recheck")
            {
                File.WriteAllText(Path.Combine(_source, "setup.exe"), "changed");
            }
        });

        var result = await pipeline.RunAsync(Request(), Ct);

        result.Status.ShouldBe(RunStatus.Failed);
        result.FailedStep.ShouldBe("source-recheck");
        result.Error.ShouldNotBeNull().ShouldContain("setup.exe");
        Directory.Exists(_output).ShouldBeFalse();
    }

    [Fact]
    public async Task A_changed_image_with_the_same_size_and_time_is_caught_by_the_recheck()
    {
        var pipeline = Pipeline(beforeStep: step =>
        {
            if (step == "source-recheck")
            {
                SourceManifestTests.Rewrite(Path.Combine(_source, "sources", "install.wim"), "MIW");
            }
        });

        var result = await pipeline.RunAsync(Request(), Ct);

        result.Status.ShouldBe(RunStatus.Failed);
        result.FailedStep.ShouldBe("source-recheck");
        Directory.Exists(_output).ShouldBeFalse();
    }

    [Fact]
    public async Task A_damaged_reused_base_file_is_caught_while_assembling()
    {
        (await Pipeline().RunAsync(Request(), Ct)).Status.ShouldBe(RunStatus.Published);
        Directory.Delete(_output, recursive: true);
        SourceManifestTests.Rewrite(Path.Combine(_paths.Base, "setup.exe"), "SETUP");

        var result = await Pipeline().RunAsync(Request(), Ct);

        result.Status.ShouldBe(RunStatus.Failed);
        result.FailedStep.ShouldBe("assemble");
        result.Error.ShouldNotBeNull().ShouldContain("setup.exe");
        Directory.Exists(_output).ShouldBeFalse();
    }

    [Fact]
    public async Task A_damaged_reused_base_image_is_deleted_and_copied_again_next_time()
    {
        (await Pipeline().RunAsync(Request(), Ct)).Status.ShouldBe(RunStatus.Published);
        SourceManifestTests.Rewrite(Path.Combine(_paths.Base, "sources", "install.wim"), "MIW");

        var failed = await Pipeline().RunAsync(Request(), Ct);
        failed.Status.ShouldBe(RunStatus.Failed);
        failed.FailedStep.ShouldBe("working-copy");
        Directory.Exists(_paths.Base).ShouldBeFalse();

        var log = new List<string>();
        (await Pipeline(log).RunAsync(Request(), Ct)).Status.ShouldBe(RunStatus.Published);
        log.ShouldNotContain("base copy reused");
    }

    [Fact]
    public async Task An_interrupted_run_blocks_new_runs_until_recovery_restores_the_previous_result()
    {
        (await Pipeline().RunAsync(Request(), Ct)).Status.ShouldBe(RunStatus.Published);
        var previous = await SourceManifest.ComputeAsync(_output, Ct);

        // Simulates a crash in the middle of publication: the old result was moved aside, the image is still mounted.
        var crashed = Guid.NewGuid();
        var publication = PublicationPaths.For(_output, crashed);
        Directory.Move(_output, publication.Previous);
        Directory.CreateDirectory(publication.Staging);
        Directory.CreateDirectory(_paths.Run(crashed).Working);
        Directory.CreateDirectory(_paths.Mount(ImageId));
        _servicing.Mounted.Add(_paths.Mount(ImageId));
        _servicing.OwnedHives.Add(HiveLoadName.Create(crashed, OfflineHive.Software));
        var store = new StateStore<WorkspaceState>(_paths.StateFile);
        store.Save(store.Load().State! with
        {
            Target = TargetStatus.InProgress,
            LastRun = new RunRecord(crashed, RunStatus.Running, _output, ImageId, null, null),
        });

        await Should.ThrowAsync<WorkspaceRecoveryRequiredException>(() => Pipeline().RunAsync(Request(), Ct));

        var recovery = await new RunRecovery(_paths, _servicing).RecoverAsync(Ct);

        recovery.Outcome.ShouldBe(RecoveryOutcome.Recovered, string.Join("; ", recovery.Problems));
        _servicing.Calls[^1].ShouldBe("unmount discard");
        _servicing.Mounted.ShouldBeEmpty();
        _servicing.OwnedHives.ShouldBeEmpty();
        SourceManifest.Compare(previous, await SourceManifest.ComputeAsync(_output, Ct)).ShouldBeEmpty();
        Directory.GetFileSystemEntries(_dir.Combine("out")).ShouldBe([_output]);
        State().Target.ShouldBe(TargetStatus.NeedsRebuild);
        State().LastRun!.Status.ShouldBe(RunStatus.Failed);

        (await Pipeline().RunAsync(Request(), Ct)).Status.ShouldBe(RunStatus.Published);
    }

    [Fact]
    public async Task Recovery_without_an_interrupted_run_does_nothing()
    {
        (await new RunRecovery(_paths, _servicing).RecoverAsync(Ct)).Outcome.ShouldBe(RecoveryOutcome.NothingToDo);
    }

    private const string Clipchamp = "Clipchamp.Clipchamp_4.3.10120.0_neutral_~_yxz26nhyzhsrt";
    private const string Weather = "Microsoft.BingWeather_4.54.63029.0_neutral_~_8wekyb3d8bbwe";

    [Fact]
    public async Task Planned_packages_are_removed_after_mount_and_before_the_registry()
    {
        _servicing.Provisioned.AddRange([Clipchamp, Weather]);

        var result = await Pipeline().RunAsync(AppxRequest(Clipchamp, Clipchamp, Weather), Ct);

        result.Status.ShouldBe(RunStatus.Published, result.Error);
        _servicing.Calls.ShouldBe(["mount", "appx-list", "appx-remove Clipchamp.Clipchamp", "appx-list", "registry", "unmount commit", "export"]);
        _servicing.Provisioned.ShouldBe([Weather]);
    }

    [Fact]
    public async Task An_inventory_that_changed_since_the_preview_stops_the_run_before_any_removal()
    {
        _servicing.Provisioned.AddRange([Clipchamp, Weather]);
        var request = AppxRequest(Clipchamp, Clipchamp);

        var result = await Pipeline().RunAsync(request, Ct);

        result.Status.ShouldBe(RunStatus.Failed);
        result.FailedStep.ShouldBe("appx");
        result.Error.ShouldNotBeNull().ShouldContain("changed");
        _servicing.Calls.ShouldNotContain(c => c.StartsWith("appx-remove", StringComparison.Ordinal));
        _servicing.Calls.ShouldContain("unmount discard");
        _servicing.Calls.ShouldNotContain("unmount commit");
    }

    [Fact]
    public async Task A_package_still_listed_after_its_removal_fails_the_run()
    {
        _servicing.Provisioned.AddRange([Clipchamp]);
        _servicing.IgnoreRemoval = true;

        var result = await Pipeline().RunAsync(AppxRequest(Clipchamp, Clipchamp), Ct);

        result.Status.ShouldBe(RunStatus.Failed);
        result.FailedStep.ShouldBe("appx");
        _servicing.Calls.ShouldContain("unmount discard");
    }

    /// <param name="remove">The package to remove.</param>
    /// <param name="inventory">What the preview saw; the run compares it with the image.</param>
    private RunRequest AppxRequest(string remove, params string[] inventory)
    {
        var packages = inventory.Distinct().Select(n => PackageIdentity.Parse(n)!).Select(i => new ProvisionedAppxPackage(i.Name, i)).ToList();
        var family = PackageIdentity.Parse(remove)!.FamilyName;
        var plan = new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid)
            .Plan(new PlanRequest(Guid.NewGuid(), 1, ImageId, [ValidationMarker.M1, new RemoveProvisionedAppx(family)]) { Appx = new AppxInventory(packages, []) })
            .Plan.ShouldNotBeNull();
        return new RunRequest(_source, _output, 1, plan);
    }

    private WorkspaceState State() => new StateStore<WorkspaceState>(_paths.StateFile).Load().State!;

    private RunPipeline Pipeline(List<string>? log = null, Action<string>? beforeStep = null) =>
        new(_paths, _servicing, Guid.NewGuid)
        {
            Log = log is null ? null : log.Add,
            BeforeStep = beforeStep is null ? null : (step, _) =>
            {
                beforeStep(step);
                return Task.CompletedTask;
            },
        };

    private RunRequest Request(InjectedFailure failure = InjectedFailure.None)
    {
        var plan = new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid)
            .Plan(new PlanRequest(Guid.NewGuid(), 1, ImageId, [ValidationMarker.M1])).Plan!;
        return new RunRequest(_source, _output, 1, plan, failure);
    }
}
