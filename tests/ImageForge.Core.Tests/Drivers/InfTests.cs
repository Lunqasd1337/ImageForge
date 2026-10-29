using System.Text;
using ImageForge.Core.Drivers;

namespace ImageForge.Core.Tests.Drivers;

public sealed class InfTests
{
    internal const string Storage = """
        ; Sample storage miniport, comments ; and "quoted ; text" are handled
        [Version]
        Signature   = "$WINDOWS NT$"
        Class       = SCSIAdapter
        ClassGuid   = {4D36E97B-E325-11CE-BFC1-08002BE10318}
        Provider    = %VENDOR%
        DriverVer   = 06/21/2024,100.95.104.26000
        CatalogFile = viostor.cat
        PnpLockdown = 1

        [SourceDisksNames.amd64]
        1 = %DiskName%,,,\amd64

        [SourceDisksFiles.amd64]
        viostor.sys = 1,,

        [Manufacturer]
        %VENDOR% = Models, NTamd64.10.0, \
                   NTarm64

        [Models.NTamd64.10.0]
        %DeviceDesc% = scsi_inst, PCI\VEN_1AF4&DEV_1001

        [Strings]
        VENDOR     = "Red Hat, Inc."
        DiskName   = "VirtIO Storage %% Disk"
        DeviceDesc = "Red Hat VirtIO SCSI controller"
        """;

    private static DriverPackageInfo Analyze(string text, params string[] present) =>
        DriverPackage.Analyze(@"amd64\viostor.inf", text, p => present.Contains(p, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Parses_sections_values_continuations_and_strings()
    {
        var inf = InfParser.Parse(Storage);

        inf.Value("Version", "Provider").ShouldBe("Red Hat, Inc.");
        inf.Value("version", "CLASS").ShouldBe("SCSIAdapter");
        inf.Lines("Manufacturer").ShouldHaveSingleItem().Values.ShouldBe(["Models", "NTamd64.10.0", "NTarm64"]);
        inf.Value("SourceDisksNames.amd64", "1").ShouldBe("VirtIO Storage % Disk");
        inf.Lines("SourceDisksNames.amd64").Single().Values.ShouldBe(["VirtIO Storage % Disk", "", "", @"\amd64"]);
        inf.Lines("Models.NTamd64.10.0").Single().Values.ShouldBe(["scsi_inst", @"PCI\VEN_1AF4&DEV_1001"]);
        inf.HasSection("Strings").ShouldBeTrue();
        inf.HasSection("Nope").ShouldBeFalse();
    }

    [Fact]
    public void Reads_the_facts_of_a_driver_package()
    {
        var package = Analyze(Storage, @"amd64\viostor.cat", @"amd64\amd64\viostor.sys");

        package.Problems.ShouldBeEmpty();
        var inf = package.Inf.ShouldNotBeNull();
        inf.Class.ShouldBe("SCSIAdapter");
        inf.ClassGuid.ShouldBe("{4D36E97B-E325-11CE-BFC1-08002BE10318}");
        inf.Provider.ShouldBe("Red Hat, Inc.");
        inf.DriverDate.ShouldBe("06/21/2024");
        inf.DriverVersion.ShouldBe("100.95.104.26000");
        inf.CatalogFile.ShouldBe("viostor.cat");
        inf.Architectures.ShouldBe(["amd64", "arm64"]);
        package.Files.ShouldBe([@"amd64\amd64\viostor.sys"]);
    }

    [Fact]
    public void A_package_with_missing_files_or_catalog_is_reported()
    {
        Analyze(Storage, @"amd64\viostor.cat").Problems.ShouldHaveSingleItem().ShouldContain("viostor.sys");
        Analyze(Storage, @"amd64\amd64\viostor.sys").Problems.ShouldHaveSingleItem().ShouldContain("viostor.cat");
    }

    [Fact]
    public void A_package_without_x64_or_without_a_catalog_is_reported()
    {
        var x86 = Storage.Replace("Models, NTamd64.10.0, \\\n           NTarm64", "Models", StringComparison.Ordinal);
        Analyze(x86, @"amd64\viostor.cat", @"amd64\amd64\viostor.sys").Problems.ShouldContain(p => p.Contains("x64", StringComparison.Ordinal));

        var unsigned = Storage.Replace("CatalogFile = viostor.cat", string.Empty, StringComparison.Ordinal);
        var package = Analyze(unsigned, @"amd64\amd64\viostor.sys");
        package.Problems.ShouldBeEmpty();
        package.Inf!.CatalogFile.ShouldBeNull();
        package.IsUnsigned.ShouldBeTrue();
    }

    [Fact]
    public void The_architecture_specific_catalog_wins()
    {
        var text = Storage.Replace("CatalogFile = viostor.cat", "CatalogFile = any.cat\nCatalogFile.NTamd64 = x64.cat", StringComparison.Ordinal);

        Analyze(text, @"amd64\x64.cat", @"amd64\amd64\viostor.sys").Inf!.CatalogFile.ShouldBe("x64.cat");
    }

    [Theory]
    [InlineData("[Strings]\nA=1\n")]
    [InlineData("[Version]\nSignature=\"$Windows 95$\"\nClass=Net\n")]
    public void A_file_that_is_not_an_nt_driver_inf_is_reported(string text)
    {
        var package = Analyze(text);

        package.Inf.ShouldBeNull();
        package.Problems.ShouldNotBeEmpty();
    }

    [Fact]
    public void Decodes_utf16_utf8_and_ansi_files()
    {
        InfText.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("[Version]\nÄ")]).ShouldBe("[Version]\nÄ");
        InfText.Decode([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("[Version]\nÄ")]).ShouldBe("[Version]\nÄ");
        InfText.Decode(Encoding.Unicode.GetBytes("[Version]")).ShouldBe("[Version]");
        InfText.Decode(Encoding.Latin1.GetBytes("[Version]\nÄ")).ShouldBe("[Version]\nÄ");
    }
}
