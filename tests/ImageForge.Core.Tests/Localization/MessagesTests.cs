using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using ImageForge.Core;
using Shouldly;
using Xunit;

namespace ImageForge.Core.Tests.Localization;

public sealed partial class MessagesTests
{
    private static readonly ResourceManager Resources = new("ImageForge.Core.Messages", typeof(Messages).Assembly);

    [Fact]
    public void Every_resource_has_one_member_with_as_many_arguments_as_placeholders()
    {
        var members = typeof(Messages).GetMembers(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m is PropertyInfo || (m is MethodInfo method && !method.IsSpecialName && method.Name != nameof(Messages.Get)))
            .ToDictionary(m => m.Name, m => m is MethodInfo method ? method.GetParameters().Length : 0);

        var resources = Resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)!
            .Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

        resources.Keys.Order().ShouldBe(members.Keys.Order());
        foreach (var (name, text) in resources)
        {
            text.ShouldNotBeNullOrWhiteSpace(name);
            var placeholders = Placeholder().Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
            var count = placeholders.Count == 0 ? 0 : placeholders.Max() + 1;
            count.ShouldBe(members[name], name);
        }
    }

    [Fact]
    public void Formats_arguments_into_the_text()
    {
        Messages.WimSplit(1, 3).ShouldBe("WIM разбит на части (1 из 3), DISM не примет такой пакет");
        Messages.AppxRequiredByKept("A", "B, C").ShouldBe("A нужна приложениям, которые остаются: B, C. Отметьте и их или оставьте A.");
    }

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();
}
