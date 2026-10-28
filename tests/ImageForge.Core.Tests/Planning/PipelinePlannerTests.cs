using ImageForge.Core.Changes;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;

namespace ImageForge.Core.Tests.Planning;

public sealed class PipelinePlannerTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("8d7c1b7e-0000-4000-8000-000000000001");
    private static readonly Guid ImageId = Guid.Parse("8d7c1b7e-0000-4000-8000-000000000002");
    private static readonly Guid PlanId = Guid.Parse("8d7c1b7e-0000-4000-8000-000000000003");

    private static PipelinePlanner Planner() => new(new RegistryPathMapper(), () => PlanId);

    private static PlanRequest Request(long revision = 7, params ImageChange[] changes) =>
        new(WorkspaceId, revision, ImageId, changes);

    [Fact]
    public void M1_marker_is_the_typed_REG_SZ_write_from_the_plan()
    {
        var marker = ValidationMarker.M1;

        marker.KeyPath.ShouldBe(@"HKLM\SOFTWARE\ImageForge\Validation");
        marker.ValueName.ShouldBe("Milestone");
        marker.Value.Type.ShouldBe(RegistryValueType.Sz);
        marker.Value.Text.ShouldBe("M1");
    }

    [Fact]
    public void Plans_the_M1_marker_as_load_set_unload_of_the_offline_SOFTWARE_hive()
    {
        var result = Planner().Plan(Request(7, ValidationMarker.M1));

        result.Diagnostics.ShouldBeEmpty();
        var plan = result.Plan.ShouldNotBeNull();
        plan.PlanId.ShouldBe(PlanId);
        plan.WorkspaceId.ShouldBe(WorkspaceId);
        plan.WorkspaceRevision.ShouldBe(7);
        plan.ImageId.ShouldBe(ImageId);
        plan.Steps.ShouldBe(
        [
            new LoadOfflineHive(OfflineHive.Software, @"Windows\System32\config\SOFTWARE"),
            new SetOfflineRegistryValue(OfflineHive.Software, @"ImageForge\Validation", "Milestone", RegistryValue.Sz("M1")),
            new UnloadOfflineHive(OfflineHive.Software),
        ]);
    }

    [Fact]
    public void Preview_names_the_offline_hive_file_and_the_exact_write()
    {
        var plan = Planner().Plan(Request(7, ValidationMarker.M1)).Plan.ShouldNotBeNull();

        plan.Preview.ShouldBe(
        [
            new RegistryWritePreview(
                @"HKLM\SOFTWARE\ImageForge\Validation",
                OfflineHive.Software,
                @"Windows\System32\config\SOFTWARE",
                @"ImageForge\Validation",
                "Milestone",
                RegistryValue.Sz("M1")),
        ]);
    }

    [Fact]
    public void Several_writes_to_one_hive_share_a_single_load_and_keep_their_order()
    {
        var first = new SetRegistryValue(@"HKLM\SOFTWARE\A", "x", RegistryValue.Sz("1"));
        var second = new SetRegistryValue(@"HKLM\SOFTWARE\A", "x", RegistryValue.Sz("2"));

        var plan = Planner().Plan(Request(1, first, second)).Plan.ShouldNotBeNull();

        plan.Steps.Count.ShouldBe(4);
        plan.Steps[0].ShouldBeOfType<LoadOfflineHive>();
        plan.Steps[1].ShouldBe(new SetOfflineRegistryValue(OfflineHive.Software, "A", "x", RegistryValue.Sz("1")));
        plan.Steps[2].ShouldBe(new SetOfflineRegistryValue(OfflineHive.Software, "A", "x", RegistryValue.Sz("2")));
        plan.Steps[3].ShouldBeOfType<UnloadOfflineHive>();
    }

    [Fact]
    public void An_empty_change_set_is_not_planned()
    {
        var result = Planner().Plan(Request(1));

        result.Plan.ShouldBeNull();
        result.Diagnostics.Single().Code.ShouldBe(PlanningCodes.EmptyChangeSet);
    }

    [Theory]
    [InlineData(@"HKU\S-1-5-18\Software", PlanningCodes.RegistryRootForbidden)]
    [InlineData(@"HKLM\SAM\X", PlanningCodes.RegistryRootForbidden)]
    [InlineData(@"HKXX\SOFTWARE", PlanningCodes.RegistryRootUnknown)]
    [InlineData(@"HKLM\SOFTWARE\\X", PlanningCodes.RegistryPathMalformed)]
    public void Unmappable_registry_paths_block_the_whole_plan(string keyPath, string expectedCode)
    {
        var bad = new SetRegistryValue(keyPath, "v", RegistryValue.Sz("d"));

        var result = Planner().Plan(Request(1, ValidationMarker.M1, bad));

        result.Plan.ShouldBeNull();
        var diagnostic = result.Diagnostics.Single();
        diagnostic.Code.ShouldBe(expectedCode);
        diagnostic.ChangeIndex.ShouldBe(1);
    }

    [Fact]
    public void A_reg_file_becomes_ordered_steps_per_hive_with_a_preview_for_each_change()
    {
        var bytes = new RegistryValue(RegistryValueType.Binary, [1, 2]);
        var result = Planner().Plan(Request(
            1,
            new CreateRegistryKey(@"HKLM\SOFTWARE\A"),
            new SetRegistryValue(@"HKLM\SYSTEM\CurrentControlSet\Services\S", "Start", bytes),
            new DeleteRegistryValue(@"HKLM\SOFTWARE\A", ""),
            new DeleteRegistryKey(@"HKCR\.old")));

        var plan = result.Plan.ShouldNotBeNull();
        plan.Steps.ShouldBe(
        [
            new LoadOfflineHive(OfflineHive.Software, @"Windows\System32\config\SOFTWARE"),
            new CreateOfflineRegistryKey(OfflineHive.Software, "A"),
            new DeleteOfflineRegistryValue(OfflineHive.Software, "A", ""),
            new DeleteOfflineRegistryKey(OfflineHive.Software, @"Classes\.old"),
            new UnloadOfflineHive(OfflineHive.Software),
            new LoadOfflineHive(OfflineHive.System, @"Windows\System32\config\SYSTEM"),
            new SetOfflineRegistryValue(OfflineHive.System, @"Services\S", "Start", new RegistryValue(RegistryValueType.Binary, [1, 2]), UnderCurrentControlSet: true),
            new UnloadOfflineHive(OfflineHive.System),
        ]);
        plan.Preview.Select(p => p.Kind).ShouldBe([RegistryChangeKind.CreateKey, RegistryChangeKind.SetValue, RegistryChangeKind.DeleteValue, RegistryChangeKind.DeleteKey]);
        plan.Preview[1].UnderCurrentControlSet.ShouldBeTrue();
        plan.Preview[3].ClassesRootPolicy.ShouldBeTrue();
        plan.Preview[0].ValueName.ShouldBeNull();
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE")]
    [InlineData(@"HKCU")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet")]
    public void Deleting_a_hive_root_is_blocked(string keyPath)
    {
        Planner().Plan(Request(1, new DeleteRegistryKey(keyPath))).Diagnostics.Single().Code.ShouldBe(PlanningCodes.RegistryKeyDeleteRoot);
    }

    [Fact]
    public void Value_bytes_are_part_of_the_fingerprint()
    {
        var a = Planner().Plan(Request(1, new SetRegistryValue(@"HKLM\SOFTWARE\A", "b", new RegistryValue(RegistryValueType.Binary, [1])))).Plan!;
        var b = Planner().Plan(Request(1, new SetRegistryValue(@"HKLM\SOFTWARE\A", "b", new RegistryValue(RegistryValueType.Binary, [2])))).Plan!;
        var c = Planner().Plan(Request(1, new SetRegistryValue(@"HKLM\SOFTWARE\A", "b", new RegistryValue(RegistryValueType.None, [1])))).Plan!;

        a.Fingerprint.ShouldNotBe(b.Fingerprint);
        a.Fingerprint.ShouldNotBe(c.Fingerprint);
    }

    [Fact]
    public void Value_types_outside_0_to_b_are_rejected()
    {
        Planner().Plan(Request(1, new SetRegistryValue(@"HKLM\SOFTWARE\A", "b", new RegistryValue((RegistryValueType)0xC, [])))).Diagnostics.Single().Code.ShouldBe(PlanningCodes.RegistryValueInvalid);
    }

    [Fact]
    public void REG_SZ_data_with_embedded_NUL_is_rejected()
    {
        var bad = new SetRegistryValue(@"HKLM\SOFTWARE\A", "v", RegistryValue.Sz("a\0b"));

        Planner().Plan(Request(1, bad)).Diagnostics.Single().Code.ShouldBe(PlanningCodes.RegistryValueInvalid);
    }

    [Fact]
    public void Value_names_longer_than_16383_characters_are_rejected()
    {
        var bad = new SetRegistryValue(@"HKLM\SOFTWARE\A", new string('n', 16384), RegistryValue.Sz("d"));

        Planner().Plan(Request(1, bad)).Diagnostics.Single().Code.ShouldBe(PlanningCodes.RegistryValueInvalid);
    }

    [Fact]
    public void The_default_value_is_allowed()
    {
        var change = new SetRegistryValue(@"HKLM\SOFTWARE\A", "", RegistryValue.Sz("d"));

        Planner().Plan(Request(1, change)).Plan.ShouldNotBeNull();
    }

    [Fact]
    public void Fingerprint_is_stable_for_the_same_input_and_ignores_the_plan_id()
    {
        var a = new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid).Plan(Request(7, ValidationMarker.M1)).Plan!;
        var b = new PipelinePlanner(new RegistryPathMapper(), Guid.NewGuid).Plan(Request(7, ValidationMarker.M1)).Plan!;

        a.PlanId.ShouldNotBe(b.PlanId);
        a.Fingerprint.ShouldBe(b.Fingerprint);
        a.Fingerprint.Length.ShouldBe(64);
    }

    [Fact]
    public void Fingerprint_changes_with_revision_or_data()
    {
        var baseline = Planner().Plan(Request(7, ValidationMarker.M1)).Plan!.Fingerprint;
        var otherRevision = Planner().Plan(Request(8, ValidationMarker.M1)).Plan!.Fingerprint;
        var otherData = Planner().Plan(Request(7, ValidationMarker.M1 with { Value = RegistryValue.Sz("M2") })).Plan!.Fingerprint;

        otherRevision.ShouldNotBe(baseline);
        otherData.ShouldNotBe(baseline);
    }

    [Fact]
    public void Fingerprint_is_not_fooled_by_separator_characters_in_data()
    {
        var a = new SetRegistryValue(@"HKLM\SOFTWARE\A", "x|y", RegistryValue.Sz("z"));
        var b = new SetRegistryValue(@"HKLM\SOFTWARE\A", "x", RegistryValue.Sz("y|z"));

        Planner().Plan(Request(1, a)).Plan!.Fingerprint.ShouldNotBe(Planner().Plan(Request(1, b)).Plan!.Fingerprint);
    }
}
