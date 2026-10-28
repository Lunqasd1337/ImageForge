using System.Buffers;
using System.Globalization;

namespace ImageForge.Core.Appx;

/// <summary>
/// The parts of a package full name, Name_Version_Architecture_ResourceId_PublisherId. Parsing is strict, because the
/// name is passed to Dism.exe and a removal must name exactly one package.
/// </summary>
public sealed record PackageIdentity(string FullName, string Name, Version Version, string Architecture, string ResourceId, string PublisherId)
{
    private static readonly SearchValues<char> NameChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.-");

    // Publisher ids are 13 characters of Crockford base32, lowercase.
    private static readonly SearchValues<char> PublisherChars = SearchValues.Create("0123456789abcdefghjkmnpqrstvwxyz");

    private static readonly string[] Architectures = ["neutral", "x86", "x64", "arm", "arm64"];

    /// <summary>Name_PublisherId: the identity that stays the same across versions and architectures.</summary>
    public string FamilyName => $"{Name}_{PublisherId}";

    public static PackageIdentity? Parse(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        var parts = fullName.Split('_');
        if (parts.Length != 5)
        {
            return null;
        }

        var (name, version, architecture, resource, publisher) = (parts[0], parts[1], parts[2], parts[3], parts[4]);
        if (name.Length is < 3 or > 50 || name.AsSpan().ContainsAnyExcept(NameChars)
            || !TryParseVersion(version, out var parsedVersion)
            || !Architectures.Contains(architecture, StringComparer.Ordinal)
            || (resource.Length > 0 && resource != "~" && (resource.Length > 30 || resource.AsSpan().ContainsAnyExcept(NameChars)))
            || publisher.Length != 13 || publisher.AsSpan().ContainsAnyExcept(PublisherChars))
        {
            return null;
        }

        return new PackageIdentity(fullName, name, parsedVersion, architecture, NormalizeResourceId(resource), publisher);
    }

    /// <summary>DISM prints "~" for a package without a resource id.</summary>
    public static string NormalizeResourceId(string resourceId) => resourceId == "~" ? string.Empty : resourceId;

    private static bool TryParseVersion(string text, out Version version)
    {
        version = new Version();
        var parts = text.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        var numbers = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (parts[i].Length is 0 or > 5
                || !ushort.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            numbers[i] = number;
        }

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }
}
