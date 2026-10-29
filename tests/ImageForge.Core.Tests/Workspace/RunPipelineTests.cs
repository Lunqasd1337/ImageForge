using ImageForge.Core.Appx;
using ImageForge.Core.Changes;
using ImageForge.Core.Drivers;
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

    private const string Math = "MathRecognizer~~~~0.0.1.0";

    [Fact]
    public async Task Planned_features_and_capabilities_change_before_the_registry_and_dism_extras_are_logged()
    {
        _servicing.Features["IIS-WebServerRole"] = "Disabled";
        _servicing.Features["IIS-WebServer"] = "Disabled";
        _servicing.Features["SMB1Protocol"] = "Enabled";
        _servicing.FeatureParents["IIS-WebServerRole"] = [];
        _servicing.FeatureParents["IIS-WebServer"] = ["IIS-WebServerRole"];
        _servicing.FeatureParents["SMB1Protocol"] = [];
        _servicing.Capabilities[Math] = "Installed";
        var request = await FeatureRequest([new EnableWindowsFeature("IIS-WebServerRole"), new DisableWindowsFeature("SMB1Protocol"), new RemoveCapability(Math)]);
        _servicing.AlsoEnabled.Add("IIS-WebServer");
        var log = new List<string>();

        var result = await Pipeline(log).RunAsync(request, Ct);

        result.Status.ShouldBe(RunStatus.Published, result.Error);
        _servicing.Calls.ShouldBe(["mount", "capability-list", "capability-remove MathRecognizer", "capability-list",
            "feature-list", "disable SMB1Protocol", "enable IIS-WebServerRole", "feature-list", "registry", "unmount commit", "export"]);
        log.ShouldContain("feature IIS-WebServerRole: Disabled -> Enable Pending");
        log.ShouldContain("feature SMB1Protocol: Enabled -> Disabled");
        log.ShouldContain("also changed by DISM IIS-WebServer: Disabled -> Enable Pending");
    }

    [Fact]
    public async Task Features_that_changed_since_the_preview_stop_the_run_before_any_change()
    {
        _servicing.Features["SMB1Protocol"] = "Enabled";
        _servicing.FeatureParents["SMB1Protocol"] = [];
        var request = await FeatureRequest([new DisableWindowsFeature("SMB1Protocol")]);
        _servicing.Features["TelnetClient"] = "Disabled";

        var result = await Pipeline().RunAsync(request, Ct);

        result.Status.ShouldBe(RunStatus.Failed);
        result.FailedStep.ShouldBe("features");
        _servicing.Calls.ShouldNotContain(c => c.StartsWith("disable", StringComparison.Ordinal));
        _servicing.Calls.ShouldContain("unmount discard");
    }

    [Fact]
    public async Task NetFx3_is_enabled_from_the_sxs_folder_of_the_media()
    {
        const string cab = "media/sources/sxs/microsoft-windows-netfx3-ondemand-package~31bf3856ad364e35~amd64~~.cab";
        _dir.WriteFile(cab, "CAB");
        _servicing.Features["NetFx3"] = "Disabled with Payload Removed";
        _servicing.FeatureParents["NetFx3"] = [];
        var request = await FeatureRequest([new EnableWindowsFeature("NetFx3")], mediaHasNetFx3: true);

        var result = await Pipeline().RunAsync(request, Ct);

        result.Status.ShouldBe(RunStatus.Published, result.Error);
        _servicing.Calls.ShouldContain("enable NetFx3 from sxs");
    }

    [Fact]
    public async Task A_feature_dism_left_off_fails_the_run()
    {
        _servicing.Features["TelnetClient"] = "Disabled";
        _servicing.FeatureParents["TelnetClient"] = [];
        var request = await FeatureRequest([new EnableWindowsFeature("TelnetClient")]);
        _servicing.IgnoreFeatureChanges = true;

        var result = await Pipeline().RunAsync(request, Ct);

        result.Status.ShouldBe(RunStatus.Failed);
        result.FailedStep.ShouldBe("features");
        result.Error.ShouldNotBeNull().ShouldContain("TelnetClient");
        _servicing.Calls.ShouldContain("unmount discard");
    }

    [Fact]
    public async Task Capabilities_are_added_from_the_fod_folder_and_what_dism_adds_along_is_logged()
    {
        _servicing.Capabilities["Language.Speech~~~fr-FR~0.0.1.0"] = "Not Present";
        _servicing.Capabilities["Language.Basic~~~fr-FR~0.0.1.0"] = "Not Present";
        _servicing.Capabilities[Math] = "Installed";
        var request = await FeatureRequest([new AddCapability("Language.Speech~~~fr-FR~0.0.1.0"), new RemoveCapability(Math)], fod: Fod());
        _servicing.AlsoAdded.Add("Language.Basic~~~fr-FR~0.0.1.0");
        var log = new List<string>();

        var result = await Pipeline(log).RunAsync(request, Ct);

        result.Status.ShouldBe(RunStatus.Published, result.Error);
        _servicing.Calls.ShouldBe(["mount", "capability-list", "capability-remove MathRecognizer", "capability-add Language.Speech from fod",
            "capability-list", "registry", "unmount commit", "export"]);
        log.ShouldContain("capability Language.Speech~~~fr-FR~0.0.1.0: Not Present -> Installed");
        log.ShouldContain("capability MathRecognizer~~~~0.0.1.0: Installed -> Not Present");
        log.ShouldContain("also changed by DISM Language.Basic~~~fr-FR~0.0.1.0: Not Present -> Installed");
    }

    [Fact]
    public async Task A_fod_folder_that_went_away_stops_the_run_before_adding()
    {
        _servicing.Capabilities["OpenSSH.Server~~~~0.0.1.0"] = "Not Present";
        var fod = Fod();
        var request = await FeatureRequest([new AddCapability("OpenSSH.Server~~~~0.0.1.0")], fod: fod);
        Directory.Delete(Path.Combine(fod, "metadata"), recursive: true);

        var result = await Pipeline().RunAsync(request, Ct);

        result.Status.ShouldBe(RunStatus.Failed);
        result.FailedStep.ShouldBe("capabilities");
        result.Error.ShouldNotBeNull().ShouldContain("metadata");
        _servicing.Calls.ShouldNotContain(c => c.StartsWith("capability-add", StringComparison.Ordinal));
        _servicing.Calls.ShouldContain("unmount discard");
    }

    [Fact]
    public async Task Drivers_are_removed_then_added_and_their_new_names_are_logged()
    {
        _servicing.Drivers.Add(new InstalledDriver("oem0.inf", @"C:\m\x\old.inf", "Net", "Vendor", "1.0", DateTime.UnixEpoch, true, "Signed"));
        var folder = DriverPackages();
        var request = DriverRequest(folder, new RemoveDriverPackage("oem0.inf", AllowBootCritical: true), new AddDriverPackage(@"net\netkvm.inf"));
        var log = new List<string>();

        var result = await Pipeline(log).RunAsync(request, Ct);

        result.Status.ShouldBe(RunStatus.Published, result.Error);
        _servicing.Calls.ShouldBe(["mount", "driver-list", "driver-remove oem0.inf", "driver-add netkvm.inf", "driver-list", "registry", "unmount commit", "export"]);
        log.ShouldContain(@"driver net\netkvm.inf -> oem0.inf (Signed)");
        log.ShouldContain(l => l.StartsWith("remove driver oem0.inf", StringComparison.Ordinal) && l.EndsWith("(boot-critical)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_package_the_image_already_holds_is_logged_and_the_run_goes_on()
    {
        var request = DriverRequest(DriverPackages(), new AddDriverPackage(@"net\netkvm.inf"));
        _servicing.DriverAlreadyPresent = true;
        var log = new List<string>();

        (await Pipeline(log).RunAsync(request, Ct)).Status.ShouldBe(RunStatus.Published);
        log.ShouldContain(@"driver net\netkvm.inf: no new name in the image (DISM already holds this package)");
    }

    [Fact]
    public async Task A_package_changed_after_the_preview_is_not_added()
    {
        var folder = DriverPackages();
        var request = DriverRequest(folder, new AddDriverPackage(@"net\netkvm.inf"));
        File.WriteAllText(Path.Combine(folder, "net", "amd64", "viostor.sys"), "replaced");

        var result = await Pipeline().RunAsync(request, Ct);

        result.Status.ShouldBe(RunStatus.Failed);
        result.FailedStep.ShouldBe("drivers");
        result.Error.ShouldNotBeNull().ShouldContain(@"net\netkvm.inf");
        _servicing.Calls.ShouldNotContain(c => c.StartsWith("driver-add", StringComparison.Ordinal));
        _servicing.Calls.ShouldContain("unmount discard");
    }

    [Fact]
    public async Task Drivers_that_changed_since_the_preview_stop_the_run()
    {
        var request = DriverRequest(DriverPackages(), new AddDriverPackage(@"net\netkvm.inf"));
        _servicing.Drivers.Add(new InstalledDriver("oem5.inf", @"C:\m\x\other.inf", "Net", "Vendor", "1.0", DateTime.UnixEpoch, false, "Signed"));

        var result = await Pipeline().RunAsync(request, Ct);

        result.FailedStep.ShouldBe("drivers");
        result.Error.ShouldNotBeNull().ShouldContain("plan again");
    }

    // One signed package, net\netkvm.inf, with the storage sample's catalog and file layout.
    private string DriverPackages()
    {
        _dir.WriteFile("drivers/net/netkvm.inf", Drivers.InfTests.Storage);
        _dir.WriteFile("drivers/net/viostor.cat", "CAT");
        _dir.WriteFile("drivers/net/amd64/viostor.sys", "SYS");
        return _dir.Combine("drivers");
    }

    private RunRequest DriverRequest(string folder, params ImageChange[] changes)
    {
        var plan = new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid)
            .Plan(new PlanRequest(Guid.NewGuid(), 1, ImageId, [ValidationMarker.M1, .. changes])
            {
                Drivers = new DriverInventory([.. _servicing.Drivers], []),
                DriverSource = DriverFolder.Scan(folder),
            });
        return new RunRequest(_source, _output, 1, plan.Plan.ShouldNotBeNull(string.Join("; ", plan.Diagnostics.Select(d => d.Message))));
    }

    private string Fod()
    {
        _dir.WriteFile("fod/OpenSSH-Server-Package~31bf3856ad364e35~amd64~~.cab", "CAB");
        _dir.WriteFile("fod/metadata/DesktopTargetCompDB_FOD_Neutral.xml.cab", "CAB");
        return _dir.Combine("fod");
    }

    private async Task<RunRequest> FeatureRequest(IReadOnlyList<ImageChange> changes, bool mediaHasNetFx3 = false, string? fod = null)
    {
        var features = await _servicing.ReadFeatureDetailsAsync("m", await _servicing.GetFeaturesAsync("m", Ct), Ct);
        var capabilities = await _servicing.GetCapabilitiesAsync("m", Ct);
        _servicing.Calls.Clear();
        var plan = new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid)
            .Plan(new PlanRequest(Guid.NewGuid(), 1, ImageId, [ValidationMarker.M1, .. changes])
            {
                Features = features,
                Capabilities = capabilities,
                MediaHasNetFx3 = mediaHasNetFx3,
                FodSource = fod,
            });
        return new RunRequest(_source, _output, 1, plan.Plan.ShouldNotBeNull(string.Join("; ", plan.Diagnostics.Select(d => d.Message))));
    }

    /// <param name="remove">The package to remove.</param>
    /// <param name="inventory">What the preview saw; the run compares it with the image.</param>
    private RunRequest AppxRequest(string remove, params string[] inventory)
    {
        var packages = inventory.Distinct().Select(n => Appx.AppxPlanningTests.Package(n)).ToList();
        var family = PackageIdentity.Parse(remove)!.FamilyName;
        var plan = new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid)
            .Plan(new PlanRequest(Guid.NewGuid(), 1, ImageId, [ValidationMarker.M1, new RemoveProvisionedAppx(family)]) { Appx = new AppxInventory(packages, []) { InboxDependencies = [] } })
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
