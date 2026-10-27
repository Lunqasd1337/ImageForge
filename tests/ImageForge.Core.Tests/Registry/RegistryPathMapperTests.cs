using ImageForge.Core.Registry;

namespace ImageForge.Core.Tests.Registry;

public sealed class RegistryPathMapperTests
{
    private readonly RegistryPathMapper _mapper = new();

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\ImageForge\Validation", @"ImageForge\Validation")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\ImageForge\Validation", @"ImageForge\Validation")]
    [InlineData(@"hklm\software\ImageForge", "ImageForge")]
    [InlineData(@"HKLM\SOFTWARE\Classes\.txt", @"Classes\.txt")]
    [InlineData(@"HKLM\SOFTWARE\Key/With/Slashes", "Key/With/Slashes")]
    [InlineData(@"HKLM\SOFTWARE", "")]
    public void Maps_HKLM_SOFTWARE_to_the_offline_SOFTWARE_hive(string importPath, string expectedSubKey)
    {
        var result = _mapper.Map(importPath);

        result.IsSuccess.ShouldBeTrue();
        result.Location.ShouldNotBeNull();
        result.Location.Hive.ShouldBe(OfflineHive.Software);
        result.Location.HiveFile.ShouldBe(@"Windows\System32\config\SOFTWARE");
        result.Location.SubKey.ShouldBe(expectedSubKey);
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE2\X")]
    [InlineData(@"HKLM\SOFTWAREX")]
    [InlineData(@"HKLMX\SOFTWARE\X")]
    [InlineData(@"HKEY_LOCAL_MACHINE_SOFTWARE\X")]
    [InlineData(@"HKLM")]
    public void Does_not_map_lookalike_paths(string importPath)
    {
        var result = _mapper.Map(importPath);

        result.IsSuccess.ShouldBeFalse();
        result.Location.ShouldBeNull();
    }

    [Theory]
    [InlineData(@"HKLM\SAM\X")]
    [InlineData(@"HKLM\SECURITY\Policy")]
    [InlineData(@"HKLM\HARDWARE\DESCRIPTION")]
    [InlineData(@"HKCC\Software")]
    [InlineData(@"HKEY_CURRENT_CONFIG\Software")]
    public void Blocks_forbidden_roots(string importPath)
    {
        _mapper.Map(importPath).Error.ShouldBe(RegistryMappingError.ForbiddenRoot);
    }

    [Theory]
    [InlineData(@"HKLM\SYSTEM\Setup")]
    [InlineData(@"HKCU\Software\Microsoft")]
    [InlineData(@"HKEY_CURRENT_USER\Software\Classes\x")]
    [InlineData(@"HKU\.DEFAULT\Control Panel")]
    [InlineData(@"HKCR\.txt")]
    public void Reports_roots_planned_after_M1_as_not_supported_yet(string importPath)
    {
        _mapper.Map(importPath).Error.ShouldBe(RegistryMappingError.NotSupportedYet);
    }

    [Theory]
    [InlineData(@"HKXX\SOFTWARE")]
    [InlineData(@"SOFTWARE\ImageForge")]
    public void Reports_unknown_roots(string importPath)
    {
        _mapper.Map(importPath).Error.ShouldBe(RegistryMappingError.UnknownRoot);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"HKLM\SOFTWARE\\ImageForge")]
    [InlineData(@"HKLM\SOFTWARE\ImageForge\")]
    [InlineData(@"\HKLM\SOFTWARE")]
    [InlineData(" HKLM\\SOFTWARE")]
    [InlineData("HKLM\\SOFTWARE\\Bad\0Name")]
    public void Rejects_malformed_paths(string importPath)
    {
        _mapper.Map(importPath).Error.ShouldBe(RegistryMappingError.MalformedPath);
    }

    [Fact]
    public void Rejects_key_names_longer_than_255_characters()
    {
        var path = @"HKLM\SOFTWARE\" + new string('k', 256);

        _mapper.Map(path).Error.ShouldBe(RegistryMappingError.MalformedPath);
    }

    [Fact]
    public void Accepts_key_names_of_exactly_255_characters()
    {
        var path = @"HKLM\SOFTWARE\" + new string('k', 255);

        _mapper.Map(path).IsSuccess.ShouldBeTrue();
    }
}
