using System.Text.RegularExpressions;
using ImageForge.Core.Registry;

namespace ImageForge.Core.Execution;

/// <summary>
/// Names under which offline hives are loaded into HKLM. The prefix marks them as owned by ImageForge,
/// so recovery only ever unloads its own hives.
/// </summary>
public static partial class HiveLoadName
{
    public static string Create(Guid runId, OfflineHive hive) =>
        $"ImageForge_{runId:N}_{hive.ToString().ToUpperInvariant()}";

    public static bool IsOwned(string name) => OwnedPattern().IsMatch(name);

    [GeneratedRegex(@"^ImageForge_[0-9a-f]{32}_[A-Z]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex OwnedPattern();
}
