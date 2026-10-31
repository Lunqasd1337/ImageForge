using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace ImageForge.Core.Tests.Localization;

/// <summary>
/// The window cannot be built off Windows, so its resources are checked as text: every Ui member and x:Uid has its
/// resw entries, every entry is used, and no Russian text is left in code or XAML (plan, section 14).
/// </summary>
public sealed partial class AppResourcesTests
{
    private static readonly string Root = FindRoot();
    private static readonly string App = Path.Combine(Root, "src", "ImageForge.App");

    private static readonly Dictionary<string, string> Resources = XDocument.Load(Path.Combine(App, "Strings", "Resources.resw"))
        .Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

    [Fact]
    public void Every_Ui_member_has_a_text_with_as_many_placeholders_as_arguments()
    {
        var members = UiMember().Matches(File.ReadAllText(Path.Combine(App, "Ui.cs")))
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Success ? m.Groups[2].Value.Split(',').Length : 0);
        members.ShouldNotBeEmpty();

        foreach (var (name, arguments) in members)
        {
            Resources.ShouldContainKey(name);
            Placeholders(Resources[name]).ShouldBe(arguments, name);
        }

        var uids = Xaml().SelectMany(text => Uid().Matches(text)).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var uid in uids)
        {
            Resources.Keys.ShouldContain(k => k.StartsWith(uid + ".", StringComparison.Ordinal), uid);
        }

        foreach (var key in Resources.Keys)
        {
            var dot = key.IndexOf('.', StringComparison.Ordinal);
            (dot < 0 ? members.ContainsKey(key) : uids.Contains(key[..dot])).ShouldBeTrue($"{key} is not used");
        }
    }

    [Fact]
    public void No_Russian_text_is_left_in_code_or_xaml()
    {
        var files = Directory.EnumerateFiles(App, "*.cs", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "src", "ImageForge.Core"), "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "src", "ImageForge.Engine"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var found = files
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && Cyrillic().IsMatch(CodePart(l.Text)))
            .Select(l => $"{Path.GetRelativePath(Root, l.File)}:{l.Line}")
            .Concat(Xaml().Where(text => Cyrillic().IsMatch(text)).Select(_ => "MainWindow.xaml"))
            .ToList();

        found.ShouldBeEmpty();
    }

    private static string CodePart(string line)
    {
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment >= 0 && !line[..comment].Contains('"', StringComparison.Ordinal) ? line[..comment] : line;
    }

    private static IEnumerable<string> Xaml() => Directory.EnumerateFiles(App, "*.xaml").Select(File.ReadAllText);

    private static int Placeholders(string text)
    {
        var indexes = Placeholder().Matches(text).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        return indexes.Count == 0 ? 0 : indexes.Max() + 1;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ImageForge.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("ImageForge.slnx not found above the test output.");
    }

    [GeneratedRegex(@"public static string (\w+)(?:\(([^)]*)\))? =>")]
    private static partial Regex UiMember();

    [GeneratedRegex(@"x:Uid=""(\w+)""")]
    private static partial Regex Uid();

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("[А-Яа-яЁё]")]
    private static partial Regex Cyrillic();
}
