using System.Text;
using ImageForge.Core.Contracts;
using ImageForge.Core.Drivers;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry.RegFile;
using ImageForge.Core.Updates;
using ImageForge.Core.Workspace;

namespace ImageForge.Core.Tests.Workspace;

public sealed class WorkspaceSessionTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeServicing _servicing = new();
    private readonly WorkspaceSession _session;
    private readonly OpenWorkspaceRequest _folders;

    public WorkspaceSessionTests()
    {
        _dir.WriteFile("media/setup.exe", "setup");
        _dir.WriteFile("media/sources/install.wim", "WIM");
        _folders = new OpenWorkspaceRequest(_dir.Combine("media"), _dir.Combine("work"), _dir.Combine("out", "Win11"));
        _session = new WorkspaceSession(_ => _servicing, Guid.NewGuid, dir => new TestPaths(dir), (_, _, _) => []);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _session.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void A_work_folder_has_one_writer_until_its_session_moves_on()
    {
        _session.Open(_folders).Problems.ShouldBeEmpty();
        using var second = new WorkspaceSession(_ => _servicing, Guid.NewGuid, dir => new TestPaths(dir), (_, _, _) => []);

        second.Open(_folders).Problems.ShouldHaveSingleItem().Kind.ShouldBe(LocationProblemKind.InUse);

        _session.Open(_folders with { WorkDir = _dir.Combine("work2") }).Problems.ShouldBeEmpty();
        second.Open(_folders).Problems.ShouldBeEmpty();
    }

    [Fact]
    public void A_work_folder_on_a_network_drive_is_refused_and_a_synced_source_is_a_warning()
    {
        using var network = new WorkspaceSession(_ => _servicing, Guid.NewGuid, dir => new TestPaths(dir), (_, _, _) => [])
        {
            Volumes = _ => new VolumeFacts(VolumeKind.Network, "NTFS"),
        };
        network.Open(_folders).Problems.ShouldContain(p => p.Kind == LocationProblemKind.NetworkVolume && p.Location == WorkspaceLocations.WorkDir);

        using var synced = new WorkspaceSession(_ => _servicing, Guid.NewGuid, dir => new TestPaths(dir), (_, _, _) => [])
        {
            IsSynced = path => path == _folders.SourceDir,
        };
        var summary = synced.Open(_folders);
        summary.Problems.ShouldBeEmpty();
        summary.Warnings.ShouldHaveSingleItem().Kind.ShouldBe(LocationProblemKind.SyncedFolder);
    }

    [Fact]
    public async Task The_shown_plan_runs_and_reports_progress()
    {
        _session.Open(_folders).Problems.ShouldBeEmpty();
        var plan = _session.PlanM1();
        plan.Preview.ShouldHaveSingleItem().ValueName.ShouldBe("Milestone");
        var lines = new List<string>();

        var outcome = await _session.StartAsync(Start(plan), new SyncProgress(lines), Ct);

        outcome.Rejection.ShouldBe(StartPlanRejection.None);
        outcome.Status.ShouldBe(RunStatus.Published, outcome.Error);
        outcome.PublishedPath.ShouldBe(_folders.OutputDir);
        lines.ShouldContain("publish...");
        _session.Summary().Target.ShouldBe(TargetStatus.Completed);
    }

    [Fact]
    public async Task Pro_is_serviced_by_default_and_every_edition_is_exported()
    {
        File.WriteAllBytes(_dir.Combine("media/sources/install.wim"), Updates.WimArchiveTests.BuildWim(ImageEditionsTests.FixtureXml, images: 5));

        var summary = _session.Open(_folders);
        await _session.InspectAsync(null, Ct);
        (await _session.StartAsync(Start(_session.PlanM1()), null, Ct)).Status.ShouldBe(RunStatus.Published);

        summary.Editions.Count.ShouldBe(5);
        summary.ImageIndex.ShouldBe(3);
        _servicing.MountedIndexes.ShouldBe([3, 3]);
        _servicing.Exported.ShouldBe([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task A_chosen_edition_is_serviced()
    {
        File.WriteAllBytes(_dir.Combine("media/sources/install.wim"), Updates.WimArchiveTests.BuildWim(ImageEditionsTests.FixtureXml, images: 5));

        _session.Open(_folders with { ImageIndex = 5 }).ImageIndex.ShouldBe(5);
        await _session.InspectAsync(null, Ct);

        _servicing.MountedIndexes.ShouldBe([5]);
    }

    [Fact]
    public void An_unreadable_install_wim_falls_back_to_the_first_image()
    {
        var summary = _session.Open(_folders);

        summary.Editions.ShouldBeEmpty();
        summary.ImageIndex.ShouldBe(1);
    }

    [Fact]
    public async Task A_plan_cannot_start_twice_or_after_the_workspace_changed()
    {
        _session.Open(_folders);
        var plan = _session.PlanM1();
        (await _session.StartAsync(Start(plan), null, Ct)).Status.ShouldBe(RunStatus.Published);

        (await _session.StartAsync(Start(plan), null, Ct)).Rejection.ShouldBe(StartPlanRejection.UnknownPlan);

        var fresh = _session.PlanM1();
        _session.Open(_folders);
        (await _session.StartAsync(Start(fresh), null, Ct)).Rejection.ShouldBe(StartPlanRejection.UnknownPlan);
    }

    [Fact]
    public async Task A_stale_revision_is_rejected()
    {
        _session.Open(_folders);
        var plan = _session.PlanM1();

        var outcome = await _session.StartAsync(new StartPlanRequest(plan.PlanId, plan.WorkspaceRevision - 1, Guid.NewGuid()), null, Ct);

        outcome.Rejection.ShouldBe(StartPlanRejection.ExpectedRevisionMismatch);
    }

    [Fact]
    public async Task A_retried_start_returns_the_same_operation_instead_of_running_again()
    {
        _session.Open(_folders);
        var plan = _session.PlanM1();
        var request = Start(plan);

        var first = await _session.StartAsync(request, null, Ct);
        var retry = await _session.StartAsync(request, null, Ct);

        retry.ShouldBe(first);
        _servicing.Calls.Count(c => c == "mount").ShouldBe(1);
    }

    [Fact]
    public async Task Starting_without_an_open_workspace_is_rejected()
    {
        var outcome = await _session.StartAsync(new StartPlanRequest(Guid.NewGuid(), 1, Guid.NewGuid()), null, Ct);

        outcome.Rejection.ShouldBe(StartPlanRejection.NotOpen);
    }

    [Fact]
    public void Unusable_folders_open_nothing()
    {
        var session = new WorkspaceSession(_ => _servicing, Guid.NewGuid, dir => new TestPaths(dir),
            (_, _, _) => [new LocationProblem(LocationProblemKind.Nested, WorkspaceLocations.WorkDir, WorkspaceLocations.Source)]);

        session.Open(_folders).Problems.ShouldHaveSingleItem().Kind.ShouldBe(LocationProblemKind.Nested);
        Should.Throw<InvalidOperationException>(session.PlanM1);
    }

    [Fact]
    public async Task A_failed_run_marks_the_target_and_a_new_plan_can_run()
    {
        _session.Open(_folders);
        _session.Failure = InjectedFailure.BeforePublish;
        var failed = await _session.StartAsync(Start(_session.PlanM1()), null, Ct);
        failed.Status.ShouldBe(RunStatus.Failed);
        failed.FailedStep.ShouldBe("publish");
        _session.Summary().Target.ShouldBe(TargetStatus.NeedsRebuild);

        _session.Failure = InjectedFailure.None;
        (await _session.StartAsync(Start(_session.PlanM1()), null, Ct)).Status.ShouldBe(RunStatus.Published);
    }

    [Fact]
    public async Task WhenIdle_waits_for_the_run_and_its_cleanup()
    {
        _session.Open(_folders);
        var plan = _session.PlanM1();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await cts.CancelAsync();

        var run = _session.StartAsync(Start(plan), null, cts.Token);
        await _session.WhenIdleAsync();

        run.IsCompleted.ShouldBeTrue();
        (await run).Status.ShouldBe(RunStatus.Failed);
        _servicing.Mounted.ShouldBeEmpty();
        await _session.WhenIdleAsync();
    }

    [Fact]
    public async Task A_reg_file_is_planned_and_its_changes_run()
    {
        _session.Open(_folders);

        var plan = _session.PlanRegFile(RegFile("[HKEY_LOCAL_MACHINE\\SOFTWARE\\X]\r\n\"a\"=dword:00000001\r\n"));

        plan.Diagnostics.ShouldBeEmpty();
        plan.FileDiagnostics.ShouldBeEmpty();
        plan.FileEncoding.ShouldBe("UTF-16");
        plan.Preview.Select(p => p.SubKey).ShouldBe(["X", "X"]);
        (await _session.StartAsync(Start(plan), null, Ct)).Status.ShouldBe(RunStatus.Published);
    }

    [Fact]
    public async Task A_broken_reg_file_shows_its_problems_and_drops_the_previous_plan()
    {
        _session.Open(_folders);
        var previous = _session.PlanM1();

        var plan = _session.PlanRegFile(RegFile("\"a\"=dword:1\r\n"));

        plan.PlanId.ShouldBe(Guid.Empty);
        plan.FileDiagnostics.Single().Code.ShouldBe(RegFileCodes.ValueOutsideKey);
        (await _session.StartAsync(Start(previous), null, Ct)).Rejection.ShouldBe(StartPlanRejection.UnknownPlan);
    }

    [Fact]
    public void A_reg_file_whose_paths_cannot_be_applied_gets_planning_diagnostics()
    {
        _session.Open(_folders);

        var plan = _session.PlanRegFile(RegFile("[HKEY_LOCAL_MACHINE\\SAM\\X]\r\n\"a\"=dword:1\r\n"));

        plan.PlanId.ShouldBe(Guid.Empty);
        plan.FileDiagnostics.ShouldBeEmpty();
        plan.Diagnostics.ShouldNotBeEmpty();
    }

    private const string Weather = "Microsoft.BingWeather_4.54.63029.0_neutral_~_8wekyb3d8bbwe";
    private const string VcLibs = "Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe";

    [Fact]
    public async Task Inspected_packages_can_be_planned_for_removal_and_removed()
    {
        _servicing.Provisioned.AddRange([Weather, VcLibs]);
        _servicing.Dependencies[Weather] = ["Microsoft.VCLibs.140.00_8wekyb3d8bbwe"];
        _session.Open(_folders);

        var inspection = await _session.InspectAsync(null, Ct);

        inspection.Problems.ShouldBeEmpty();
        inspection.Packages.Select(p => p.PackageName).ShouldBe([Weather, VcLibs]);
        inspection.Packages.ShouldAllBe(p => p.BlockedReason == null);
        inspection.Packages[1].NeededBy.ShouldBe(["Microsoft.BingWeather"]);
        _session.Plan(new PlanOptions(null, ["Microsoft.VCLibs.140.00_8wekyb3d8bbwe"])).Diagnostics
            .ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.AppxRequiredByKept);

        var plan = _session.Plan(new PlanOptions(null, ["Microsoft.BingWeather_8wekyb3d8bbwe"]));

        plan.Diagnostics.ShouldBeEmpty();
        plan.Preview.ShouldBeEmpty();
        plan.AppxRemovals.ShouldHaveSingleItem().PackageName.ShouldBe(Weather);
        (await _session.StartAsync(Start(plan), null, Ct)).Status.ShouldBe(RunStatus.Published);
        _servicing.Provisioned.ShouldBe([VcLibs]);
    }

    [Fact]
    public void Removal_before_inspection_is_blocked()
    {
        _session.Open(_folders);

        var plan = _session.Plan(new PlanOptions(null, ["Microsoft.BingWeather_8wekyb3d8bbwe"]));

        plan.PlanId.ShouldBe(Guid.Empty);
        plan.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.AppxInventoryMissing);
    }

    [Fact]
    public async Task Reopening_the_workspace_forgets_the_inventory()
    {
        _servicing.Provisioned.Add(Weather);
        _session.Open(_folders);
        await _session.InspectAsync(null, Ct);

        _session.Open(_folders);

        _session.Plan(new PlanOptions(null, ["Microsoft.BingWeather_8wekyb3d8bbwe"])).Diagnostics
            .ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.AppxInventoryMissing);
    }

    [Fact]
    public async Task A_failed_inspection_is_reported_and_leaves_no_inventory()
    {
        _servicing.Provisioned.Add(Weather);
        _session.Open(_folders);
        await _session.InspectAsync(null, Ct);
        _servicing.FailAppxList = true;

        var inspection = await _session.InspectAsync(null, Ct);

        inspection.Packages.ShouldBeEmpty();
        inspection.Problems.ShouldHaveSingleItem().ShouldContain("exit code 87");
        _servicing.Mounted.ShouldBeEmpty();
        _session.Plan(new PlanOptions(null, ["Microsoft.BingWeather_8wekyb3d8bbwe"])).PlanId.ShouldBe(Guid.Empty);
    }

    [Fact]
    public async Task A_reg_file_and_removals_plan_together()
    {
        _servicing.Provisioned.Add(Weather);
        _session.Open(_folders);
        await _session.InspectAsync(null, Ct);

        var plan = _session.Plan(new PlanOptions(RegFile("[HKEY_LOCAL_MACHINE\\SOFTWARE\\X]\r\n\"a\"=dword:00000001\r\n"), ["Microsoft.BingWeather_8wekyb3d8bbwe"]));

        plan.Preview.Count.ShouldBe(2);
        plan.AppxRemovals.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Inspected_features_and_capabilities_are_planned_and_changed()
    {
        _servicing.Features["IIS-WebServerRole"] = "Disabled";
        _servicing.Features["IIS-WebServer"] = "Disabled";
        _servicing.Features["SMB1Protocol"] = "Enabled";
        _servicing.Features["Odd"] = "Superseded";
        _servicing.FeatureParents["IIS-WebServerRole"] = [];
        _servicing.FeatureParents["IIS-WebServer"] = ["IIS-WebServerRole"];
        _servicing.FeatureParents["SMB1Protocol"] = [];
        _servicing.FeatureParents["Odd"] = [];
        _servicing.Capabilities["MathRecognizer~~~~0.0.1.0"] = "Installed";
        _servicing.Capabilities["Language.OCR~~~fr-FR~0.0.1.0"] = "Not Present";
        _session.Open(_folders);

        var inspection = await _session.InspectAsync(null, Ct);

        inspection.FeatureProblems.ShouldBeEmpty();
        inspection.Features.Single(f => f.Name == "SMB1Protocol").IsOn.ShouldBeTrue();
        inspection.Features.Single(f => f.Name == "Odd").BlockedReason.ShouldNotBeNull();
        inspection.Capabilities.ShouldHaveSingleItem().Name.ShouldBe("MathRecognizer~~~~0.0.1.0");

        var plan = _session.Plan(new PlanOptions(null)
        {
            EnableFeatures = ["IIS-WebServer"],
            DisableFeatures = ["SMB1Protocol"],
            RemoveCapabilities = ["MathRecognizer~~~~0.0.1.0"],
        });

        plan.Diagnostics.ShouldBeEmpty();
        var changes = plan.FeatureChanges.ShouldNotBeNull();
        changes.Enable.ShouldBe(["IIS-WebServerRole", "IIS-WebServer"]);
        changes.Added.ShouldBe(["IIS-WebServerRole"]);
        changes.Disable.ShouldBe(["SMB1Protocol"]);
        plan.CapabilityRemovals.ShouldBe(["MathRecognizer~~~~0.0.1.0"]);
        (await _session.StartAsync(Start(plan), null, Ct)).Status.ShouldBe(RunStatus.Published);
        _servicing.Features["IIS-WebServer"].ShouldBe("Enable Pending");
        _servicing.Features["SMB1Protocol"].ShouldBe("Disabled");
        _servicing.Capabilities["MathRecognizer~~~~0.0.1.0"].ShouldBe("Not Present");
        _servicing.Calls.ShouldContain("disable SMB1Protocol");
    }

    [Fact]
    public async Task The_payload_of_disabled_features_is_removed_only_when_asked()
    {
        _servicing.Features["SMB1Protocol"] = "Enabled";
        _servicing.FeatureParents["SMB1Protocol"] = [];
        _session.Open(_folders);
        await _session.InspectAsync(null, Ct);

        var plan = _session.Plan(new PlanOptions(null) { DisableFeatures = ["SMB1Protocol"], RemoveFeaturePayload = true });

        plan.FeatureChanges.ShouldNotBeNull().RemovePayload.ShouldBeTrue();
        (await _session.StartAsync(Start(plan), null, Ct)).Status.ShouldBe(RunStatus.Published);
        _servicing.Calls.ShouldContain("disable SMB1Protocol /Remove");
    }

    [Fact]
    public async Task Features_with_unread_parents_cannot_be_changed()
    {
        _servicing.Features["SMB1Protocol"] = "Enabled";
        _session.Open(_folders);

        var inspection = await _session.InspectAsync(null, Ct);

        inspection.Features.ShouldBeEmpty();
        inspection.FeatureProblems.ShouldHaveSingleItem();
        _session.Plan(new PlanOptions(null) { DisableFeatures = ["SMB1Protocol"] }).Diagnostics
            .ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.FeatureInventoryMissing);
    }

    [Fact]
    public async Task Not_present_capabilities_are_added_from_a_checked_fod_folder()
    {
        _servicing.Capabilities["OpenSSH.Server~~~~0.0.1.0"] = "Not Present";
        _servicing.Capabilities["MathRecognizer~~~~0.0.1.0"] = "Installed";
        _dir.WriteFile("fod/OpenSSH-Server-Package~31bf3856ad364e35~amd64~~.cab", "CAB");
        _dir.WriteFile("fod/metadata/DesktopTargetCompDB_FOD_Neutral.xml.cab", "CAB");
        _session.Open(_folders);

        var inspection = await _session.InspectAsync(null, Ct);

        inspection.AvailableCapabilities.ShouldHaveSingleItem().Name.ShouldBe("OpenSSH.Server~~~~0.0.1.0");
        inspection.Capabilities.ShouldHaveSingleItem().Name.ShouldBe("MathRecognizer~~~~0.0.1.0");
        _session.Plan(new PlanOptions(null) { AddCapabilities = ["OpenSSH.Server~~~~0.0.1.0"] }).Diagnostics
            .ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.CapabilitySourceMissing);
        _session.Plan(new PlanOptions(null) { AddCapabilities = ["OpenSSH.Server~~~~0.0.1.0"], FodSource = _dir.Combine("media") }).Diagnostics
            .ShouldHaveSingleItem().Message.ShouldContain("metadata");

        var plan = _session.Plan(new PlanOptions(null) { AddCapabilities = ["OpenSSH.Server~~~~0.0.1.0"], FodSource = _dir.Combine("fod") });

        plan.Diagnostics.ShouldBeEmpty();
        plan.CapabilityAdditions.ShouldBe(["OpenSSH.Server~~~~0.0.1.0"]);
        plan.FodSource.ShouldBe(_dir.Combine("fod"));
        (await _session.StartAsync(Start(plan), null, Ct)).Status.ShouldBe(RunStatus.Published);
        _servicing.Capabilities["OpenSSH.Server~~~~0.0.1.0"].ShouldBe("Installed");
    }

    [Fact]
    public async Task Drivers_of_the_image_are_shown_and_packages_of_a_folder_are_planned_and_added()
    {
        _servicing.Drivers.Add(new InstalledDriver("oem0.inf", @"C:\m\x\viostor.inf", "SCSIAdapter", "Red Hat, Inc.", "1.0", DateTime.UnixEpoch, true, "Signed"));
        _dir.WriteFile("drivers/net/netkvm.inf", Drivers.InfTests.Storage);
        _dir.WriteFile("drivers/net/viostor.cat", "CAT");
        _dir.WriteFile("drivers/net/amd64/viostor.sys", "SYS");
        _dir.WriteFile("drivers/copy/netkvm.inf", Drivers.InfTests.Storage);
        _dir.WriteFile("drivers/copy/viostor.cat", "CAT");
        _dir.WriteFile("drivers/copy/amd64/viostor.sys", "SYS");
        _session.Open(_folders);

        var inspection = await _session.InspectAsync(null, Ct);

        inspection.Drivers.ShouldHaveSingleItem().PublishedName.ShouldBe("oem0.inf");
        inspection.DriverProblems.ShouldBeEmpty();
        _session.Plan(new PlanOptions(null) { RemoveDrivers = ["oem0.inf"] }).Diagnostics
            .ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.DriverBootCritical);
        _session.Plan(new PlanOptions(null) { AddDrivers = [@"net\netkvm.inf"] }).Diagnostics
            .ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.DriverSourceMissing);

        var plan = _session.Plan(new PlanOptions(null)
        {
            AddDrivers = [@"net\netkvm.inf", @"copy\netkvm.inf"],
            DriverFolder = _dir.Combine("drivers"),
            RemoveDrivers = ["oem0.inf"],
            AllowBootCritical = true,
        });

        plan.Diagnostics.ShouldBeEmpty();
        var drivers = plan.Drivers.ShouldNotBeNull();
        drivers.Add.ShouldHaveSingleItem().SameContent.ShouldBe([@"copy\netkvm.inf"]);
        drivers.Remove.ShouldHaveSingleItem().BootCritical.ShouldBeTrue();
        drivers.Warnings.ShouldNotBeEmpty();
        (await _session.StartAsync(Start(plan), null, Ct)).Status.ShouldBe(RunStatus.Published);
        _servicing.Calls.ShouldContain("driver-remove oem0.inf");
        _servicing.Calls.ShouldContain("driver-add netkvm.inf");
    }

    [Fact]
    public async Task The_installed_update_is_shown_and_an_update_of_a_folder_is_added_and_cleaned_up()
    {
        _servicing.Packages.Add(new InstalledPackage("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9457.1.0", "Installed", "Update"));
        _servicing.Packages.Add(new InstalledPackage("Package_for_ServicingStack_9441~31bf3856ad364e35~amd64~~26100.9441.1.0", "Installed", "Update"));
        _dir.WriteFile("updates/readme.txt", "not scanned");
        File.WriteAllBytes(_dir.Combine("updates/Windows11.0-KB5134133-x64.msu"),
            Updates.WimArchiveTests.BuildWim("<WIM><IMAGE INDEX=\"1\"><NAME>content</NAME></IMAGE></WIM>"));
        _session.Open(_folders);

        var inspection = await _session.InspectAsync(null, Ct);

        inspection.Rollup.ShouldBe("26100.9457.1.0");
        inspection.ServicingStack.ShouldBe("26100.9441.1.0");
        inspection.PackageCount.ShouldBe(2);
        inspection.PackageProblems.ShouldBeEmpty();
        _session.Plan(new PlanOptions(null) { AddUpdates = ["Windows11.0-KB5134133-x64.msu"] }).Diagnostics
            .ShouldHaveSingleItem().Code.ShouldBe(PlanningCodes.UpdateSourceMissing);

        var plan = _session.Plan(new PlanOptions(null)
        {
            AddUpdates = ["Windows11.0-KB5134133-x64.msu"],
            UpdateFolder = _dir.Combine("updates"),
            CleanupComponents = true,
            ResetBase = true,
        });

        plan.Diagnostics.ShouldBeEmpty();
        plan.Updates.ShouldNotBeNull().Add.ShouldHaveSingleItem().FileName.ShouldBe("Windows11.0-KB5134133-x64.msu");
        plan.Cleanup.ShouldNotBeNull().ResetBase.ShouldBeTrue();
        (await _session.StartAsync(Start(plan), null, Ct)).Status.ShouldBe(RunStatus.Published);
        _servicing.Calls.ShouldContain("package-add Windows11.0-KB5134133-x64.msu");
        _servicing.Calls.ShouldContain("cleanup resetbase");
    }

    [Fact]
    public async Task WinRe_updates_are_planned_from_the_update_folder_and_run()
    {
        _servicing.WinRe = "WINRE";
        _dir.WriteFile("updates/readme.txt", "not scanned");
        File.WriteAllBytes(_dir.Combine("updates/Windows11.0-KB5134133-x64.msu"),
            Updates.WimArchiveTests.BuildWim("<WIM><IMAGE INDEX=\"1\"><NAME>content</NAME></IMAGE></WIM>"));
        File.WriteAllBytes(_dir.Combine("updates/windows11.0-kb5130881-x64.cab"), Updates.UpdatePackageTests.BuildCabinet([("update.mum", 10)]));
        _session.Open(_folders);

        var plan = _session.Plan(new PlanOptions(null)
        {
            AddUpdates = ["Windows11.0-KB5134133-x64.msu"],
            WinReUpdates = ["windows11.0-kb5130881-x64.cab", "Windows11.0-KB5134133-x64.msu"],
            UpdateFolder = _dir.Combine("updates"),
        });

        plan.Diagnostics.ShouldBeEmpty();
        plan.WinRe.ShouldNotBeNull().Add.Select(a => a.FileName).ShouldBe(["Windows11.0-KB5134133-x64.msu", "windows11.0-kb5130881-x64.cab"]);
        var outcome = await _session.StartAsync(Start(plan), null, Ct);
        outcome.Status.ShouldBe(RunStatus.Published, outcome.Error);
        _servicing.CommittedWinRe.ShouldBe(["WINRE+M1+export"]);
    }

    [Fact]
    public async Task Drivers_for_WinRe_are_planned_from_the_driver_folder_without_an_update_folder_and_run()
    {
        _servicing.WinRe = "WINRE";
        _dir.WriteFile("drivers/net/netkvm.inf", Drivers.InfTests.Storage);
        _dir.WriteFile("drivers/net/viostor.cat", "CAT");
        _dir.WriteFile("drivers/net/amd64/viostor.sys", "SYS");
        _session.Open(_folders);

        var plan = _session.Plan(new PlanOptions(null) { WinReDrivers = [@"net\netkvm.inf"], DriverFolder = _dir.Combine("drivers") });

        plan.Diagnostics.ShouldBeEmpty();
        plan.Drivers.ShouldBeNull();
        plan.WinRe.ShouldNotBeNull().Drivers.ShouldHaveSingleItem().InfPath.ShouldBe(@"net\netkvm.inf");
        plan.WinRe.Add.ShouldBeEmpty();
        var outcome = await _session.StartAsync(Start(plan), null, Ct);
        outcome.Status.ShouldBe(RunStatus.Published, outcome.Error);
        _servicing.Calls.ShouldContain("driver-add netkvm.inf");
        _servicing.CommittedWinRe.ShouldBe(["WINRE+M1+export"]);
    }

    private static RegFileInput RegFile(string body) =>
        new("t.reg", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Windows Registry Editor Version 5.00\r\n\r\n" + body)], RegFileEncoding.Auto, 1251);

    private static StartPlanRequest Start(PlanSummary plan) => new(plan.PlanId, plan.WorkspaceRevision, Guid.NewGuid());

    private sealed class SyncProgress(List<string> lines) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (lines)
            {
                lines.Add(value);
            }
        }
    }
}
