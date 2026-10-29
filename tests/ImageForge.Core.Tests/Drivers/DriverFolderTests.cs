using ImageForge.Core.Drivers;

namespace ImageForge.Core.Tests.Drivers;

public sealed class DriverFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("if-drivers-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Package(string folder, string sys = "driver")
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(Path.Combine(dir, "amd64"));
        File.WriteAllText(Path.Combine(dir, "viostor.inf"), InfTests.Storage);
        File.WriteAllText(Path.Combine(dir, "viostor.cat"), "catalog");
        File.WriteAllText(Path.Combine(dir, "amd64", "viostor.sys"), sys);
    }

    [Fact]
    public void Finds_packages_in_subfolders_with_a_content_hash_that_ignores_where_they_are()
    {
        Package(Path.Combine("w10", "amd64"));
        Package(Path.Combine("w11", "amd64"));
        Package("other", sys: "changed");
        File.WriteAllText(Path.Combine(_root, "readme.inf"), "[Strings]\nA=1\n");

        var folder = DriverFolder.Scan(_root);

        folder.Problem.ShouldBeNull();
        folder.Folder.ShouldBe(_root);
        folder.Packages.Select(p => p.Package.InfPath).ShouldBe(
            [@"other\viostor.inf", "readme.inf", @"w10\amd64\viostor.inf", @"w11\amd64\viostor.inf"]);
        var hashes = folder.Packages.ToDictionary(p => p.Package.InfPath, p => p.ContentHash);
        hashes[@"w10\amd64\viostor.inf"].ShouldBe(hashes[@"w11\amd64\viostor.inf"]);
        hashes[@"other\viostor.inf"].ShouldNotBe(hashes[@"w10\amd64\viostor.inf"]);
        folder.Packages.Single(p => p.Package.InfPath == "readme.inf").Package.Inf.ShouldBeNull();
        folder.Packages.Single(p => p.Package.InfPath == @"other\viostor.inf").Package.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void The_hash_of_one_package_can_be_checked_again_before_it_is_added()
    {
        Package("pkg");
        var scanned = DriverFolder.Scan(_root).Packages.Single();

        DriverFolder.ContentHash(_root, scanned.Package).ShouldBe(scanned.ContentHash);
        File.WriteAllText(Path.Combine(_root, "pkg", "amd64", "viostor.sys"), "replaced");
        DriverFolder.ContentHash(_root, scanned.Package).ShouldNotBe(scanned.ContentHash);
        File.Delete(Path.Combine(_root, "pkg", "viostor.cat"));
        DriverFolder.ContentHash(_root, scanned.Package).ShouldBeNull();
    }

    [Fact]
    public void A_missing_relative_or_empty_folder_is_a_problem()
    {
        DriverFolder.Scan(Path.Combine(_root, "nope")).Problem.ShouldNotBeNull();
        DriverFolder.Scan("relative").Problem.ShouldNotBeNull();
        DriverFolder.Scan(_root).Problem.ShouldNotBeNull();
    }
}
