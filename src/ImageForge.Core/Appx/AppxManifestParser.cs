using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace ImageForge.Core.Appx;

/// <param name="FamilyName">Name_PublisherId of the package that must stay installed.</param>
public sealed record AppxDependency(string FamilyName);

public sealed record AppxPackageManifest(
    string Name, string PublisherId, Version Version, string Architecture, bool IsFramework, IReadOnlyList<AppxDependency> Dependencies)
{
    public string FamilyName => $"{Name}_{PublisherId}";
}

/// <param name="Applications">Architecture and version of each application package in the bundle.</param>
public sealed record AppxBundleManifest(string Name, string PublisherId, Version Version, IReadOnlyList<(string Architecture, Version Version)> Applications);

/// <summary>
/// Reads AppxManifest.xml and AppxBundleManifest.xml from an image. The files come from the image, so DTDs are
/// refused and only the identity, the framework flag and the dependencies are taken; anything malformed is null.
/// </summary>
public static class AppxManifestParser
{
    public const int MaxBytes = 1024 * 1024;

    public static AppxPackageManifest? ParsePackage(string xml)
    {
        if (Load(xml) is not { Root: { } root } || root.Name.LocalName != "Package" || Identity(root) is not { } identity)
        {
            return null;
        }

        var architecture = (string?)Child(root, "Identity")!.Attribute("ProcessorArchitecture") ?? "neutral";
        var framework = string.Equals(Child(root, "Properties") is { } properties ? Child(properties, "Framework")?.Value.Trim() : null, "true", StringComparison.OrdinalIgnoreCase);
        var dependencies = new List<AppxDependency>();
        foreach (var element in Child(root, "Dependencies")?.Elements() ?? [])
        {
            if (element.Name.LocalName is not ("PackageDependency" or "MainPackageDependency"))
            {
                continue;
            }

            var name = (string?)element.Attribute("Name");
            var publisher = (string?)element.Attribute("Publisher");
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            // A main package dependency may leave out the publisher; it is then the package's own.
            var publisherId = publisher is null ? identity.PublisherId : PublisherId.From(publisher);
            dependencies.Add(new AppxDependency($"{name}_{publisherId}"));
        }

        return new AppxPackageManifest(identity.Name, identity.PublisherId, identity.Version, architecture, framework, dependencies);
    }

    public static AppxBundleManifest? ParseBundle(string xml)
    {
        if (Load(xml) is not { Root: { } root } || root.Name.LocalName != "Bundle" || Identity(root) is not { } identity)
        {
            return null;
        }

        var applications = new List<(string, Version)>();
        foreach (var package in Child(root, "Packages")?.Elements().Where(e => e.Name.LocalName == "Package") ?? [])
        {
            if ((string?)package.Attribute("Type") != "application")
            {
                continue;
            }

            if (!TryVersion((string?)package.Attribute("Version"), out var version) || (string?)package.Attribute("Architecture") is not { Length: > 0 } architecture)
            {
                return null;
            }

            applications.Add((architecture, version));
        }

        return new AppxBundleManifest(identity.Name, identity.PublisherId, identity.Version, applications);
    }

    private static (string Name, string PublisherId, Version Version)? Identity(XElement root)
    {
        var element = Child(root, "Identity");
        var name = (string?)element?.Attribute("Name");
        var publisher = (string?)element?.Attribute("Publisher");
        return name is { Length: > 0 } && publisher is { Length: > 0 } && TryVersion((string?)element!.Attribute("Version"), out var version)
            ? (name, PublisherId.From(publisher), version)
            : null;
    }

    private static XElement? Child(XElement parent, string localName) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static bool TryVersion(string? text, out Version version)
    {
        version = new Version();
        var parts = text?.Split('.');
        if (parts is not { Length: 4 })
        {
            return false;
        }

        var numbers = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!ushort.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            numbers[i] = number;
        }

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    private static XDocument? Load(string xml)
    {
        if (xml.Length > MaxBytes)
        {
            return null;
        }

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml.TrimStart()), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
