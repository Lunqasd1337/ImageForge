using ImageForge.Core.Appx;
using ImageForge.Core.Servicing;

namespace ImageForge.Core.Tests.Appx;

public sealed class ProvisionedAppxTests
{
    private const string Output = """

        Deployment Image Servicing and Management tool
        Version: 10.0.26100.1

        Image Version: 10.0.26300.1000

        Obtaining list of provisioned appx (.appx or .appxbundle) packages...

        DisplayName : Clipchamp.Clipchamp
        Version : 4.3.10120.0
        Architecture : neutral
        ResourceId : ~
        PackageName : Clipchamp.Clipchamp_4.3.10120.0_neutral_~_yxz26nhyzhsrt
        Regions : None

        DisplayName : Microsoft.WindowsStore
        Version : 22409.1401.5.0
        Architecture : x64
        ResourceId : ~
        PackageName : Microsoft.WindowsStore_22409.1401.5.0_x64__8wekyb3d8bbwe
        Regions : None

        DisplayName : Microsoft.LanguageExperiencePackru-RU
        Version : 26100.1.2.0
        Architecture : neutral
        ResourceId : ~
        PackageName : Microsoft.LanguageExperiencePackru-RU_26100.1.2.0_neutral__8wekyb3d8bbwe

        The operation completed successfully.

        """;

    [Fact]
    public void Parses_every_package_block()
    {
        var result = DismAppxListParser.Parse(Output);

        result.Problems.ShouldBeEmpty();
        result.Packages.Select(p => p.PackageName).ShouldBe(
        [
            "Clipchamp.Clipchamp_4.3.10120.0_neutral_~_yxz26nhyzhsrt",
            "Microsoft.WindowsStore_22409.1401.5.0_x64__8wekyb3d8bbwe",
            "Microsoft.LanguageExperiencePackru-RU_26100.1.2.0_neutral__8wekyb3d8bbwe",
        ]);
        var store = result.Packages[1];
        store.DisplayName.ShouldBe("Microsoft.WindowsStore");
        store.Identity.Name.ShouldBe("Microsoft.WindowsStore");
        store.Identity.Version.ShouldBe(new Version(22409, 1401, 5, 0));
        store.Identity.Architecture.ShouldBe("x64");
        store.Identity.ResourceId.ShouldBe(string.Empty);
        store.Identity.PublisherId.ShouldBe("8wekyb3d8bbwe");
        store.Identity.FamilyName.ShouldBe("Microsoft.WindowsStore_8wekyb3d8bbwe");
    }

    [Fact]
    public void The_tilde_resource_id_means_none()
    {
        var identity = PackageIdentity.Parse("Clipchamp.Clipchamp_4.3.10120.0_neutral_~_yxz26nhyzhsrt").ShouldNotBeNull();

        identity.ResourceId.ShouldBe(string.Empty);
        identity.FamilyName.ShouldBe("Clipchamp.Clipchamp_yxz26nhyzhsrt");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Name_1.0.0.0_x64_")]
    [InlineData("Name_1.0_x64__abcdefghjkmnp")]
    [InlineData("Name_1.0.0.0_mips__abcdefghjkmnp")]
    [InlineData("Na me_1.0.0.0_x64__abcdefghjkmnp")]
    [InlineData("Name_1.0.0.0_x64__abcdefghjkmnp\" /Quiet")]
    [InlineData("..\\Name_1.0.0.0_x64__abcdefghjkmnp")]
    [InlineData("Name_1.0.0.0_x64__pub")]
    public void Rejects_a_package_name_that_is_not_a_package_full_name(string name)
    {
        PackageIdentity.Parse(name).ShouldBeNull();
    }

    [Fact]
    public void A_block_whose_fields_disagree_with_its_package_name_is_a_problem()
    {
        var result = DismAppxListParser.Parse("""
            DisplayName : A.B
            Version : 1.0.0.0
            Architecture : x64
            ResourceId : ~
            PackageName : A.B_2.0.0.0_x64__abcdefghjkmnp

            The operation completed successfully.
            """);

        result.Packages.ShouldBeEmpty();
        result.Problems.ShouldHaveSingleItem().ShouldContain("A.B_2.0.0.0_x64__abcdefghjkmnp");
    }

    [Fact]
    public void A_block_without_a_package_name_is_a_problem()
    {
        var result = DismAppxListParser.Parse("""
            DisplayName : A.B
            Version : 1.0.0.0

            The operation completed successfully.
            """);

        result.Packages.ShouldBeEmpty();
        result.Problems.ShouldHaveSingleItem();
    }

    [Fact]
    public void Output_without_the_success_line_is_a_problem()
    {
        var result = DismAppxListParser.Parse(Output.Replace("The operation completed successfully.", string.Empty, StringComparison.Ordinal));

        result.Problems.ShouldContain(p => p.Contains("completed successfully", StringComparison.Ordinal));
    }

    [Fact]
    public void Store_and_App_Installer_carry_a_warning()
    {
        AppxWarnings.For(PackageIdentity.Parse("Microsoft.WindowsStore_22409.1401.5.0_x64__8wekyb3d8bbwe")!).ShouldNotBeNull();
        AppxWarnings.For(PackageIdentity.Parse("Microsoft.DesktopAppInstaller_1.24.0.0_neutral_~_8wekyb3d8bbwe")!).ShouldNotBeNull();
        AppxWarnings.For(PackageIdentity.Parse("Clipchamp.Clipchamp_4.3.10120.0_neutral_~_yxz26nhyzhsrt")!).ShouldBeNull();
    }

    [Fact]
    public void Dism_arguments_name_the_image_and_the_exact_package()
    {
        DismCliArguments.GetProvisionedAppx(@"C:\IF\mount").ShouldBe(["/English", @"/Image:C:\IF\mount", "/Get-ProvisionedAppxPackages"]);
        DismCliArguments.RemoveProvisionedAppx(@"C:\IF\mount", PackageIdentity.Parse("Clipchamp.Clipchamp_4.3.10120.0_neutral_~_yxz26nhyzhsrt")!)
            .ShouldBe(["/English", @"/Image:C:\IF\mount", "/Remove-ProvisionedAppxPackage", "/PackageName:Clipchamp.Clipchamp_4.3.10120.0_neutral_~_yxz26nhyzhsrt"]);
    }
}
