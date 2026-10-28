using System.Text;
using ImageForge.Core.Changes;
using ImageForge.Core.Execution;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry;
using ImageForge.Core.Registry.RegFile;

namespace ImageForge.Core.Tests.Registry;

public sealed class FirstLogonDeliveryTests
{
    private static readonly Guid PlanId = Guid.Parse("8d7c1b7e-0000-4000-8000-000000000003");

    private static PlanResult Plan(params ImageChange[] changes) =>
        new PipelinePlanner(new RegistryPathMapper(), () => PlanId).Plan(new PlanRequest(Guid.Empty, 1, Guid.Empty, changes));

    private static RegistryWritePreview Classes(RegistryChangeKind kind, string subKey, string? name = null, RegistryValue? value = null) =>
        new(@"HKCU\Software\Classes\" + subKey, OfflineHive.DefaultUserClasses, RegistryPathMapper.DefaultUserClassesHiveFile, subKey, name, value, kind);

    [Fact]
    public void The_user_file_is_a_v5_reg_file_in_utf16_with_bom_and_crlf()
    {
        var package = FirstLogonDelivery.UserClasses([Classes(RegistryChangeKind.SetValue, ".x", string.Empty, RegistryValue.Sz("y"))]);

        package.Content[..2].ShouldBe(new byte[] { 0xFF, 0xFE });
        Encoding.Unicode.GetString(package.Content[2..]).ShouldBe(
            "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\Classes\\.x]\r\n@=\"y\"\r\n");
    }

    [Fact]
    public void The_user_file_round_trips_through_the_parser_with_exact_types_and_bytes()
    {
        var changes = new[]
        {
            Classes(RegistryChangeKind.CreateKey, @"CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32"),
            Classes(RegistryChangeKind.SetValue, @"CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", string.Empty, RegistryValue.Sz(string.Empty)),
            Classes(RegistryChangeKind.CreateKey, ".x"),
            Classes(RegistryChangeKind.SetValue, ".x", "Цитата \"и\" \\ слеш; не комментарий", RegistryValue.Sz("строка; \\ \"")),
            Classes(RegistryChangeKind.SetValue, ".x", "Без терминатора", new RegistryValue(RegistryValueType.Sz, Encoding.Unicode.GetBytes("ab"))),
            Classes(RegistryChangeKind.SetValue, ".x", "Перевод строки", RegistryValue.Sz("a\r\nb")),
            Classes(RegistryChangeKind.SetValue, ".x", "d", new RegistryValue(RegistryValueType.Dword, [1, 2, 3, 4])),
            Classes(RegistryChangeKind.SetValue, ".x", "q", new RegistryValue(RegistryValueType.Qword, [1, 2, 3, 4, 5, 6, 7, 8])),
            Classes(RegistryChangeKind.SetValue, ".x", "e", new RegistryValue(RegistryValueType.ExpandSz, Encoding.Unicode.GetBytes("%SystemRoot%\0"))),
            Classes(RegistryChangeKind.SetValue, ".x", "big", new RegistryValue(RegistryValueType.Binary, [.. Enumerable.Range(0, 300).Select(i => (byte)i)])),
            Classes(RegistryChangeKind.SetValue, ".x", "none", new RegistryValue(RegistryValueType.None, [])),
            Classes(RegistryChangeKind.DeleteValue, ".x", "d"),
            Classes(RegistryChangeKind.DeleteKey, ".old"),
        };

        // Every change opens its key first, as a [key] line does, so the parsed operations match one to one.
        var package = FirstLogonDelivery.UserClasses(changes);
        var text = new RegFileLoader().Decode(package.Content, RegFileEncoding.Auto, 1251);
        var parsed = new RegFileParser().Parse(text.Text.ShouldNotBeNull());

        parsed.Diagnostics.ShouldBeEmpty();
        var root = @"HKEY_CURRENT_USER\Software\Classes\";
        parsed.Operations.Select(Describe).ShouldBe(changes.Select(c => c.Kind switch
        {
            RegistryChangeKind.CreateKey => $"create {root}{c.SubKey}",
            RegistryChangeKind.DeleteKey => $"delete-key {root}{c.SubKey}",
            RegistryChangeKind.SetValue => $"set {root}{c.SubKey} [{c.ValueName}] {(uint)c.NewValue!.Type}:{Convert.ToHexString(c.NewValue.Bytes)}",
            _ => $"delete-value {root}{c.SubKey} [{c.ValueName}]",
        }));
    }

    [Fact]
    public void The_component_and_file_name_follow_the_content_only()
    {
        var a = FirstLogonDelivery.UserClasses([Classes(RegistryChangeKind.CreateKey, ".x")]);
        var b = FirstLogonDelivery.UserClasses([Classes(RegistryChangeKind.CreateKey, ".x")]);
        var c = FirstLogonDelivery.UserClasses([Classes(RegistryChangeKind.CreateKey, ".y")]);

        b.FileRelativePath.ShouldBe(a.FileRelativePath);
        b.ComponentKey.ShouldBe(a.ComponentKey);
        c.FileRelativePath.ShouldNotBe(a.FileRelativePath);
        c.ComponentKey.ShouldNotBe(a.ComponentKey);
        a.FileRelativePath.ShouldStartWith(@"ProgramData\ImageForge\FirstLogon\user-classes-");
        a.FileRelativePath.ShouldEndWith(".reg");
        a.ComponentKey.ShouldStartWith(@"Microsoft\Active Setup\Installed Components\{");
    }

    [Fact]
    public void Active_setup_imports_the_file_once_per_user_with_reg_exe()
    {
        var package = FirstLogonDelivery.UserClasses([Classes(RegistryChangeKind.CreateKey, ".x")]);
        var values = package.Registration.ToDictionary(r => r.Name, r => r.Value);

        values["StubPath"].Type.ShouldBe(RegistryValueType.ExpandSz);
        values["StubPath"].Text.ShouldBe($"\"%SystemRoot%\\System32\\reg.exe\" import \"%SystemDrive%\\{package.FileRelativePath}\"");
        values["Version"].ShouldBe(RegistryValue.Sz("1,0,0,0"));
        values["IsInstalled"].ShouldBe(new RegistryValue(RegistryValueType.Dword, [1, 0, 0, 0]));
        values[string.Empty].Text.ShouldNotBeNull().ShouldContain("ImageForge");
    }

    [Fact]
    public void Planner_delivers_user_classes_at_first_logon_instead_of_loading_UsrClass_dat()
    {
        var result = Plan(
            ValidationMarker.M1,
            new SetRegistryValue(@"HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", string.Empty, RegistryValue.Sz(string.Empty)));

        result.Diagnostics.ShouldBeEmpty();
        var plan = result.Plan.ShouldNotBeNull();
        plan.Steps.ShouldNotContain(s => s is LoadOfflineHive && ((LoadOfflineHive)s).Hive == OfflineHive.DefaultUserClasses);
        var write = plan.Steps[0].ShouldBeOfType<WriteImageFile>();
        write.RelativePath.ShouldStartWith(@"ProgramData\ImageForge\FirstLogon\");

        var registration = plan.Steps.OfType<SetOfflineRegistryValue>().Where(s => s.SubKey.StartsWith(@"Microsoft\Active Setup", StringComparison.Ordinal)).ToList();
        registration.Count.ShouldBe(4);
        registration.ShouldAllBe(s => s.Hive == OfflineHive.Software);
        plan.Steps.Count(s => s is LoadOfflineHive).ShouldBe(1);

        plan.Preview.Single(p => p.Hive == OfflineHive.DefaultUserClasses).FirstLogon.ShouldBeTrue();
        plan.Preview.Count(p => p.SubKey.StartsWith(@"Microsoft\Active Setup", StringComparison.Ordinal)).ShouldBe(4);
    }

    [Fact]
    public void Plans_without_user_classes_have_no_first_logon_steps()
    {
        var plan = Plan(ValidationMarker.M1).Plan.ShouldNotBeNull();

        plan.Steps.ShouldNotContain(s => s is WriteImageFile);
        plan.Preview.ShouldNotContain(p => p.FirstLogon);
    }

    [Fact]
    public void The_fingerprint_covers_the_delivered_file()
    {
        var a = Plan(new CreateRegistryKey(@"HKCU\Software\Classes\.x")).Plan.ShouldNotBeNull();
        var b = Plan(new CreateRegistryKey(@"HKCU\Software\Classes\.y")).Plan.ShouldNotBeNull();

        a.Fingerprint.ShouldNotBe(b.Fingerprint);
    }

    [Theory]
    [InlineData(".x\ny")]
    [InlineData(".x\ry")]
    public void A_key_that_a_reg_file_cannot_hold_is_refused(string subKey)
    {
        var result = Plan(new CreateRegistryKey(@"HKCU\Software\Classes\" + subKey));

        result.Plan.ShouldBeNull();
        result.Diagnostics.Single().Code.ShouldBe(PlanningCodes.FirstLogonUnrepresentable);
    }

    [Fact]
    public void A_value_name_that_a_reg_file_cannot_hold_is_refused()
    {
        var result = Plan(new SetRegistryValue(@"HKCU\Software\Classes\.x", "a\nb", RegistryValue.Sz("y")));

        result.Plan.ShouldBeNull();
        result.Diagnostics.Single().Code.ShouldBe(PlanningCodes.FirstLogonUnrepresentable);
    }

    [Fact]
    public void Executor_writes_the_file_before_any_hive_is_loaded()
    {
        var plan = Plan(ValidationMarker.M1, new CreateRegistryKey(@"HKCU\Software\Classes\.x")).Plan.ShouldNotBeNull();
        var log = new List<string>();

        var report = new RegistryPlanExecutor(new LoggingLoader(log), f => f, new LoggingFiles(log)).Execute(plan, TestContext.Current.CancellationToken);

        report.Outcome.ShouldBe(ExecutionOutcome.Completed);
        log[0].ShouldStartWith(@"write ProgramData\ImageForge\FirstLogon\");
        log[1].ShouldBe("load Software");
    }

    [Fact]
    public void Executor_without_a_file_writer_refuses_a_plan_that_needs_one()
    {
        var plan = Plan(new CreateRegistryKey(@"HKCU\Software\Classes\.x")).Plan.ShouldNotBeNull();
        var log = new List<string>();

        var report = new RegistryPlanExecutor(new LoggingLoader(log), f => f).Execute(plan, TestContext.Current.CancellationToken);

        report.Outcome.ShouldBe(ExecutionOutcome.Failed);
        report.CompletedSteps.ShouldBe(0);
        log.ShouldBeEmpty();
    }

    private static string Describe(RegFileOperation op) => op switch
    {
        CreateRegKey k => $"create {k.KeyPath}",
        DeleteRegKey k => $"delete-key {k.KeyPath}",
        SetRegValue s => $"set {s.KeyPath} [{s.Name}] {(uint)s.Data.Type}:{Convert.ToHexString(s.Data.Bytes)}",
        DeleteRegValue d => $"delete-value {d.KeyPath} [{d.Name}]",
        _ => op.ToString(),
    };

    private sealed class LoggingFiles(List<string> log) : IImageFileWriter
    {
        public void Write(string relativePath, byte[] content) => log.Add($"write {relativePath} {content.Length}");
    }

    private sealed class LoggingLoader(List<string> log) : IOfflineHiveLoader
    {
        public bool HiveFileExists(string hiveFilePath) => true;

        public ILoadedOfflineHive Load(OfflineHive hive, string hiveFilePath)
        {
            log.Add($"load {hive}");
            return new FakeHive(hive);
        }

        private sealed class FakeHive(OfflineHive hive) : ILoadedOfflineHive
        {
            public OfflineHive Hive => hive;

            public void SetValue(string subKey, string valueName, RegistryValue value)
            {
            }

            public void DeleteValue(string subKey, string valueName)
            {
            }

            public void CreateKey(string subKey)
            {
            }

            public void DeleteKeyTree(string subKey)
            {
            }

            public uint? ReadSelectCurrent() => 1;

            public void Unload()
            {
            }
        }
    }
}
