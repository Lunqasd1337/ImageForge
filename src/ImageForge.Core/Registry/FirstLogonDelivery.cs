using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ImageForge.Core.Changes;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry.RegFile;

namespace ImageForge.Core.Registry;

/// <param name="FileRelativePath">The .reg file inside the image, relative to the mount root (the system drive).</param>
/// <param name="ComponentKey">The Active Setup component key inside the SOFTWARE hive.</param>
/// <param name="Registration">Values of <paramref name="ComponentKey"/>, in write order.</param>
public sealed record FirstLogonPackage(
    string FileRelativePath,
    byte[] Content,
    string ComponentKey,
    IReadOnlyList<(string Name, RegistryValue Value)> Registration);

/// <summary>
/// The image has no UsrClass.dat for the Default profile, so HKCU\Software\Classes cannot be written offline. These
/// changes go into a .reg file inside the image instead, and an Active Setup component imports it once for every user
/// at their first logon, before Explorer starts. No other hive is written in its place.
/// </summary>
public static class FirstLogonDelivery
{
    public const string Directory = @"ProgramData\ImageForge\FirstLogon";
    public const string ActiveSetupComponents = @"Microsoft\Active Setup\Installed Components";
    public const string UserClassesRoot = @"HKEY_CURRENT_USER\Software\Classes";

    /// <summary>True when a .reg file can hold the name exactly.</summary>
    public static bool CanHold(string name) => RegFileWriter.CanHold(name);

    public static FirstLogonPackage UserClasses(IReadOnlyList<RegistryWritePreview> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var content = RegFileWriter.Write(UserClassesRoot, changes);
        var hash = SHA256.HashData(content);
        var component = new Guid(hash.AsSpan(0, 16)).ToString("B", CultureInfo.InvariantCulture);
        var file = $@"{Directory}\user-classes-{Convert.ToHexStringLower(hash.AsSpan(0, 8))}.reg";
        return new FirstLogonPackage(
            file,
            content,
            $@"{ActiveSetupComponents}\{component}",
            [
                (string.Empty, RegistryValue.Sz(@"ImageForge: HKCU\Software\Classes")),
                ("StubPath", new RegistryValue(
                    RegistryValueType.ExpandSz,
                    Encoding.Unicode.GetBytes($"\"%SystemRoot%\\System32\\reg.exe\" import \"%SystemDrive%\\{file}\"\0"))),
                ("Version", RegistryValue.Sz("1,0,0,0")),
                ("IsInstalled", new RegistryValue(RegistryValueType.Dword, [1, 0, 0, 0])),
            ]);
    }
}
