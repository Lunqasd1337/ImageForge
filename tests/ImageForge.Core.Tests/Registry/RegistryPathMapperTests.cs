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
    [InlineData(@"HKLM\BCD00000000\Objects")]
    [InlineData(@"HKLM\SOFTWARE2\X")]
    [InlineData(@"HKCC\Software")]
    [InlineData(@"HKEY_CURRENT_CONFIG\Software")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001\Software")]
    [InlineData(@"HKU\.DEFAULTX\Software")]
    public void Blocks_forbidden_roots(string importPath)
    {
        _mapper.Map(importPath).Error.ShouldBe(RegistryMappingError.ForbiddenRoot);
    }

    [Theory]
    [InlineData(@"HKLM\SYSTEM\Setup", OfflineHive.System, @"Windows\System32\config\SYSTEM", "Setup")]
    [InlineData(@"HKLM\SYSTEM", OfflineHive.System, @"Windows\System32\config\SYSTEM", "")]
    [InlineData(@"HKLM\SYSTEM\ControlSet001\Services", OfflineHive.System, @"Windows\System32\config\SYSTEM", @"ControlSet001\Services")]
    [InlineData(@"HKCU\Software\Microsoft", OfflineHive.DefaultUser, @"Users\Default\NTUSER.DAT", @"Software\Microsoft")]
    [InlineData(@"HKEY_CURRENT_USER\Control Panel\Desktop", OfflineHive.DefaultUser, @"Users\Default\NTUSER.DAT", @"Control Panel\Desktop")]
    [InlineData(@"HKCU\Software\ClassesX", OfflineHive.DefaultUser, @"Users\Default\NTUSER.DAT", @"Software\ClassesX")]
    [InlineData(@"HKCU\Software\Classes\.txt", OfflineHive.DefaultUserClasses, @"Users\Default\AppData\Local\Microsoft\Windows\UsrClass.dat", ".txt")]
    [InlineData(@"hkcu\software\classes", OfflineHive.DefaultUserClasses, @"Users\Default\AppData\Local\Microsoft\Windows\UsrClass.dat", "")]
    [InlineData(@"HKU\.DEFAULT\Control Panel", OfflineHive.Default, @"Windows\System32\config\DEFAULT", "Control Panel")]
    public void Maps_each_root_to_its_offline_hive(string importPath, OfflineHive hive, string hiveFile, string subKey)
    {
        var location = _mapper.Map(importPath).Location.ShouldNotBeNull();

        location.Hive.ShouldBe(hive);
        location.HiveFile.ShouldBe(hiveFile);
        location.SubKey.ShouldBe(subKey);
        location.UnderCurrentControlSet.ShouldBeFalse();
        location.ClassesRootPolicy.ShouldBeFalse();
    }

    [Theory]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip", @"Services\Tcpip")]
    [InlineData(@"HKEY_LOCAL_MACHINE\system\currentcontrolset", "")]
    public void CurrentControlSet_is_left_for_the_image_to_resolve(string importPath, string subKey)
    {
        var location = _mapper.Map(importPath).Location.ShouldNotBeNull();

        location.Hive.ShouldBe(OfflineHive.System);
        location.SubKey.ShouldBe(subKey);
        location.UnderCurrentControlSet.ShouldBeTrue();
    }

    [Theory]
    [InlineData(@"HKCR\.txt", @"Classes\.txt")]
    [InlineData(@"HKEY_CLASSES_ROOT\CLSID\{0}", @"Classes\CLSID\{0}")]
    [InlineData(@"HKCR", "Classes")]
    public void HKCR_goes_to_the_machine_classes_by_policy(string importPath, string subKey)
    {
        var location = _mapper.Map(importPath).Location.ShouldNotBeNull();

        location.Hive.ShouldBe(OfflineHive.Software);
        location.SubKey.ShouldBe(subKey);
        location.ClassesRootPolicy.ShouldBeTrue();
    }

    [Theory]
    [InlineData(1u, "ControlSet001")]
    [InlineData(2u, "ControlSet002")]
    [InlineData(0u, null)]
    [InlineData(null, null)]
    public void Current_control_set_comes_only_from_Select_Current(uint? current, string? expected)
    {
        ControlSet.FromSelectCurrent(current).ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"HKXX\SOFTWARE")]
    [InlineData(@"SOFTWARE\ImageForge")]
    [InlineData(@"HKLM")]
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
