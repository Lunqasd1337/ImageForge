#pragma warning disable CA5350 // Catalog file names carry SHA-1.
using System.Security.Cryptography;
using System.Text;
using ImageForge.Core.Servicing;
using ImageForge.Core.Updates;

namespace ImageForge.Core.Tests.Updates;

public sealed class UpdatePackageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("if-updates-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Builds the header and file table of a cabinet; the folders and data blocks are not read.</summary>
    internal static byte[] BuildCabinet(IEnumerable<(string Name, uint Size)> files, ushort reserve = 0, bool utf8 = false)
    {
        var list = files.ToList();
        using var table = new MemoryStream();
        using (var w = new BinaryWriter(table, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var (name, size) in list)
            {
                w.Write(size);
                w.Write(0u);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write((ushort)(utf8 ? 0x80 : 0x20));
                w.Write((utf8 ? Encoding.UTF8 : Encoding.Latin1).GetBytes(name));
                w.Write((byte)0);
            }
        }

        var headerSize = 36 + (reserve > 0 ? 4 + reserve : 0);
        var folderSize = 8;
        using var cab = new MemoryStream();
        using (var w = new BinaryWriter(cab, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("MSCF"u8);
            w.Write(0u);
            w.Write((uint)(headerSize + folderSize + table.Length));
            w.Write(0u);
            w.Write((uint)(headerSize + folderSize));
            w.Write(0u);
            w.Write((byte)3);
            w.Write((byte)1);
            w.Write((ushort)1);
            w.Write((ushort)list.Count);
            w.Write((ushort)(reserve > 0 ? 4 : 0));
            w.Write((ushort)0);
            w.Write((ushort)0);
            if (reserve > 0)
            {
                w.Write(reserve);
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write(new byte[reserve]);
            }

            w.Write(new byte[folderSize]);
            w.Write(table.ToArray());
        }

        return cab.ToArray();
    }

    private static CabinetListing Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return Cabinet.Read(stream);
    }

    [Fact]
    public void Reads_the_file_table_of_a_cabinet_with_a_signature_reserve()
    {
        var listing = Read(BuildCabinet([("Windows11.0-KB5070001-x64.cab", 1000), ("WSUSSCAN.cab", 20)], reserve: 6144));

        listing.Problem.ShouldBeNull();
        listing.Entries.ShouldBe([new CabinetEntry("Windows11.0-KB5070001-x64.cab", 1000), new CabinetEntry("WSUSSCAN.cab", 20)]);
    }

    [Fact]
    public void Utf8_names_are_decoded()
    {
        Read(BuildCabinet([("папка\\update.mum", 5)], utf8: true)).Entries.Single().Name.ShouldBe("папка\\update.mum");
    }

    [Fact]
    public void Something_that_is_not_a_cabinet_or_is_cut_short_is_a_problem()
    {
        Read("PK\u0003\u0004 not a cabinet at all, but long enough"u8.ToArray()).Problem.ShouldNotBeNull();
        Read([0x4D, 0x53]).Problem.ShouldNotBeNull();
        var cut = BuildCabinet([("update.mum", 5)]);
        Read(cut[..^4]).Problem.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("windows11.0-kb5070001-x64_0123456789abcdef0123456789abcdef01234567.msu", 5070001, "x64", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("Windows11.0-KB5070001-x64.msu", 5070001, "x64", null)]
    [InlineData("windows11.0-kb5070002-arm64_ffffffffffffffffffffffffffffffffffffffff.msu", 5070002, "arm64", "ffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("SSU-26300.1000-x64.cab", null, "x64", null)]
    [InlineData("update.cab", null, null, null)]
    public void The_file_name_gives_the_kb_the_architecture_and_the_sha1(string name, int? kb, string? architecture, string? sha1)
    {
        var parsed = UpdateFileName.Parse(name);

        parsed.Kb.ShouldBe(kb);
        parsed.Architecture.ShouldBe(architecture);
        parsed.Sha1.ShouldBe(sha1);
    }

    [Fact]
    public void An_msu_lists_the_packages_inside_it()
    {
        var info = UpdatePackage.Analyze(
            "windows11.0-kb5070001-x64.msu",
            new CabinetListing(
            [
                new("DesktopDeployment.cab", 1), new("onepackage.AggregatedMetadata.cab", 1), new("SSU-26300.1000-x64.cab", 1),
                new("Windows11.0-KB5070001-x64.wim", 1), new("WSUSSCAN.cab", 1),
            ], null));

        info.Kind.ShouldBe(UpdateKind.Msu);
        info.Kb.ShouldBe(5070001);
        info.Problems.ShouldBeEmpty();
        info.Payloads.ShouldBe(["SSU-26300.1000-x64.cab", "Windows11.0-KB5070001-x64.wim"]);
        info.HasServicingStack.ShouldBeTrue();
    }

    [Fact]
    public void A_cab_is_a_servicing_package_only_with_update_mum()
    {
        var cab = UpdatePackage.Analyze("SSU-26300.1000-x64.cab", new CabinetListing([new("update.mum", 1), new("x.manifest", 1)], null));
        cab.Kind.ShouldBe(UpdateKind.Cab);
        cab.Problems.ShouldBeEmpty();
        cab.HasServicingStack.ShouldBeTrue();

        UpdatePackage.Analyze("drivers.cab", new CabinetListing([new("viostor.inf", 1)], null)).Problems.ShouldNotBeEmpty();
    }

    [Fact]
    public void Other_architectures_unknown_extensions_and_unreadable_files_are_problems()
    {
        UpdatePackage.Analyze("windows11.0-kb5070002-arm64.msu", new CabinetListing([new("Windows11.0-KB5070002-arm64.cab", 1)], null))
            .Problems.ShouldNotBeEmpty();
        UpdatePackage.Analyze("readme.txt", new CabinetListing([], null)).Kind.ShouldBe(UpdateKind.Unknown);
        UpdatePackage.Analyze("broken.msu", new CabinetListing([], "нет подписи MSCF")).Problems.ShouldContain(p => p.Contains("MSCF"));
        UpdatePackage.Analyze("empty.msu", new CabinetListing([new("WSUSSCAN.cab", 1)], null)).Problems.ShouldNotBeEmpty();
    }

    [Fact]
    public void The_folder_scan_hashes_each_package_and_checks_the_sha1_in_the_name()
    {
        var good = BuildCabinet([("Windows11.0-KB5070001-x64.cab", 1)]);
        var goodSha1 = Convert.ToHexStringLower(SHA1.HashData(good));
        File.WriteAllBytes(Path.Combine(_root, $"windows11.0-kb5070001-x64_{goodSha1}.msu"), good);
        File.WriteAllBytes(Path.Combine(_root, $"windows11.0-kb5070003-x64_{new string('0', 40)}.msu"), good);
        Directory.CreateDirectory(Path.Combine(_root, "ssu"));
        File.WriteAllBytes(Path.Combine(_root, "ssu", "SSU-26300.1000-x64.cab"), BuildCabinet([("update.mum", 1)]));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not an update");

        var folder = UpdateFolder.Scan(_root);

        folder.Problem.ShouldBeNull();
        folder.Packages.Select(p => p.Package.FileName).ShouldBe(
            [@"ssu\SSU-26300.1000-x64.cab", $"windows11.0-kb5070001-x64_{goodSha1}.msu", $"windows11.0-kb5070003-x64_{new string('0', 40)}.msu"]);
        var byName = folder.Packages.ToDictionary(p => p.Package.FileName);
        byName[$"windows11.0-kb5070001-x64_{goodSha1}.msu"].Package.Problems.ShouldBeEmpty();
        byName[$"windows11.0-kb5070001-x64_{goodSha1}.msu"].Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(good)));
        byName[$"windows11.0-kb5070003-x64_{new string('0', 40)}.msu"].Package.Problems.ShouldContain(p => p.Contains("SHA-1"));
        UpdateFolder.CurrentHash(_root, @"ssu\SSU-26300.1000-x64.cab").ShouldBe(byName[@"ssu\SSU-26300.1000-x64.cab"].Sha256);
        UpdateFolder.CurrentHash(_root, "gone.msu").ShouldBeNull();
    }

    [Fact]
    public void A_missing_relative_or_empty_folder_is_a_problem()
    {
        UpdateFolder.Scan(Path.Combine(_root, "nope")).Problem.ShouldNotBeNull();
        UpdateFolder.Scan("relative").Problem.ShouldNotBeNull();
        UpdateFolder.Scan(_root).Problem.ShouldNotBeNull();
    }

    [Fact]
    public void Dism_exe_gets_a_local_package_path()
    {
        DismCliArguments.GetPackages(@"C:\IF\m").ShouldBe(["/English", @"/Image:C:\IF\m", "/Get-Packages"]);
        DismCliArguments.AddPackage(@"C:\IF\m", @"C:\IF\upd\lcu")
            .ShouldBe(["/English", @"/Image:C:\IF\m", "/Add-Package", @"/PackagePath:C:\IF\upd\lcu"]);
        Should.Throw<ArgumentException>(() => DismCliArguments.AddPackage(@"C:\IF\m", @"\\server\upd"));
        Should.Throw<ArgumentException>(() => DismCliArguments.AddPackage(@"C:\IF\m", "upd"));
    }
}

public sealed class WimArchiveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("if-wim-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A WIM header and its XML resource; the offset table and metadata are not read.</summary>
    internal static byte[] BuildWim(string xml, ushort part = 1, ushort parts = 1, uint images = 1, uint bootIndex = 0)
    {
        var xmlBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
        using var wim = new MemoryStream();
        using (var w = new BinaryWriter(wim, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("MSWIM\0\0\0"u8);
            w.Write(208u);
            w.Write(0x10D00u);
            w.Write(0x80u);
            w.Write(0u);
            w.Write(new byte[16]);
            w.Write(part);
            w.Write(parts);
            w.Write(images);
            w.Write(new byte[24]);
            Resource(w, (ulong)xmlBytes.Length, 208);
            w.Write(new byte[24]);
            w.Write(bootIndex);
            w.Write(new byte[208 - 124]);
            w.Write(xmlBytes);
        }

        return wim.ToArray();
    }

    private static void Resource(BinaryWriter w, ulong size, ulong offset)
    {
        w.Write(BitConverter.GetBytes(size)[..7]);
        w.Write((byte)0);
        w.Write(offset);
        w.Write(size);
    }

    private const string Xml =
        "<WIM><TOTALBYTES>4879117191</TOTALBYTES><IMAGE INDEX=\"1\"><DIRCOUNT>3</DIRCOUNT><FILECOUNT>7</FILECOUNT>" +
        "<TOTALBYTES>5000000000</TOTALBYTES><NAME>KB5134133</NAME><DESCRIPTION>Update</DESCRIPTION></IMAGE></WIM>";

    private static WimInfo Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return WimArchive.Read(stream);
    }

    [Fact]
    public void Reads_the_header_and_the_images_from_the_xml()
    {
        var wim = Read(BuildWim(Xml));

        wim.Problem.ShouldBeNull();
        wim.TotalParts.ShouldBe(1);
        wim.Images.ShouldBe([new WimArchiveImage("KB5134133", "Update", 7, 5000000000)]);
    }

    [Fact]
    public void The_boot_index_of_a_boot_wim_is_read_from_the_header()
    {
        Read(BuildWim(Xml, images: 2, bootIndex: 2)).BootIndex.ShouldBe(2);
        Read(BuildWim(Xml)).BootIndex.ShouldBe(0);
    }

    [Fact]
    public void A_split_wim_a_wim_without_images_and_a_cut_header_are_problems()
    {
        Read(BuildWim(Xml, part: 1, parts: 2)).Problem.ShouldNotBeNull();
        Read(BuildWim("<WIM></WIM>", images: 0)).Problem.ShouldNotBeNull();
        Read(BuildWim(Xml)[..100]).Problem.ShouldNotBeNull();
        Read("MSCF and something else entirely"u8.ToArray()).Problem.ShouldNotBeNull();
    }

    [Fact]
    public void A_bad_xml_resource_does_not_hide_the_header()
    {
        var wim = Read(BuildWim("<WIM><IMAGE"));

        wim.Problem.ShouldBeNull();
        wim.Images.ShouldBeEmpty();
    }

    [Fact]
    public void An_msu_in_wim_format_is_accepted_and_left_to_dism()
    {
        var info = UpdatePackage.Analyze("Windows11.0-KB5134133-x64.msu", Read(BuildWim(Xml)));

        info.Kind.ShouldBe(UpdateKind.Msu);
        info.Container.ShouldBe(UpdateContainer.Wim);
        info.Kb.ShouldBe(5134133);
        info.Problems.ShouldBeEmpty();
        info.Payloads.ShouldBeEmpty();
        info.Wim!.Images.Single().FileCount.ShouldBe(7);

        UpdatePackage.Analyze("Windows11.0-KB5134133-x64.cab", Read(BuildWim(Xml))).Problems.ShouldNotBeEmpty();
        UpdatePackage.Analyze("split.msu", Read(BuildWim(Xml, parts: 2))).Problems.ShouldNotBeEmpty();
    }

    [Fact]
    public void The_folder_scan_tells_a_wim_msu_from_a_cabinet()
    {
        File.WriteAllBytes(Path.Combine(_root, "Windows11.0-KB5134133-x64.msu"), BuildWim(Xml));
        File.WriteAllBytes(Path.Combine(_root, "windows11.0-kb5070001-x64.msu"), UpdatePackageTests.BuildCabinet([("Windows11.0-KB5070001-x64.cab", 1)]));

        var packages = UpdateFolder.Scan(_root).Packages.ToDictionary(p => p.Package.FileName, p => p.Package);

        packages["Windows11.0-KB5134133-x64.msu"].Container.ShouldBe(UpdateContainer.Wim);
        packages["Windows11.0-KB5134133-x64.msu"].Problems.ShouldBeEmpty();
        packages["windows11.0-kb5070001-x64.msu"].Container.ShouldBe(UpdateContainer.Cabinet);
    }
}
