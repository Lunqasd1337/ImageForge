namespace ImageForge.Core.Features;

/// <param name="Features">Every feature to pass to DISM, in order: for an enable, parents before the features that
/// need them; for a disable, dependents before the features they need. Empty when nothing needs to change.</param>
/// <param name="Added">The features in <see cref="Features"/> that the user did not ask for, to show before the change.</param>
/// <param name="NeedsSource">Features in <see cref="Features"/> whose payload was removed from the image.</param>
/// <param name="Problems">Anything that stops the change; when not empty, nothing may be changed.</param>
/// <param name="Before">The state before; null when the feature was not listed.</param>
/// <param name="After">The state after; null when the feature is no longer listed.</param>
public sealed record FeatureStateChange(string Name, string? Before, string? After);

public sealed record FeatureChange(
    IReadOnlyList<string> Features, IReadOnlyList<string> Added, IReadOnlyList<string> NeedsSource, IReadOnlyList<string> Problems);

/// <summary>
/// Works out what else changes with one feature, from the direct parents of each feature (DismGetFeatureParent).
/// DISM refuses to enable a feature while a parent is off, and disabling a feature turns off what depends on it, so
/// both are shown and passed explicitly instead of through /All.
/// </summary>
public static class FeatureDependencies
{
    /// <param name="parents">Direct parents by feature name; a feature missing here has parents that were not read.</param>
    public static FeatureChange Enable(FeatureInventory inventory, IReadOnlyDictionary<string, IReadOnlyList<string>> parents, string name)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(parents);
        var features = Index(inventory);
        var problems = new List<string>();
        if (!features.TryGetValue(name, out var requested))
        {
            return Refuse($"Feature '{name}' is not in this image.");
        }

        if (requested.IsOn)
        {
            return new FeatureChange([], [], [], []);
        }

        var order = new List<WindowsFeature>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(requested);
        return Result(order, requested, problems, needsSource: true);

        void Visit(WindowsFeature feature)
        {
            if (done.Contains(feature.Name))
            {
                return;
            }

            if (!visiting.Add(feature.Name))
            {
                problems.Add($"Feature '{feature.Name}' is its own parent.");
                return;
            }

            if (!feature.IsOff || feature.State == FeatureState.DisablePending)
            {
                problems.Add($"Feature '{feature.Name}' is {feature.RawState}; it cannot be enabled now.");
            }

            if (!parents.TryGetValue(feature.Name, out var direct))
            {
                problems.Add($"The parents of feature '{feature.Name}' are unknown.");
                direct = [];
            }

            foreach (var parentName in direct)
            {
                if (!features.TryGetValue(parentName, out var parent))
                {
                    problems.Add($"Parent '{parentName}' of feature '{feature.Name}' is not in this image.");
                }
                else if (!parent.IsOn)
                {
                    Visit(parent);
                }
            }

            visiting.Remove(feature.Name);
            done.Add(feature.Name);
            order.Add(feature);
        }

        FeatureChange Refuse(string problem) => new([], [], [], [problem]);
    }

    /// <param name="parents">Direct parents of every feature in the image; what depends on a feature is known only when
    /// the parents of all of them were read.</param>
    public static FeatureChange Disable(FeatureInventory inventory, IReadOnlyDictionary<string, IReadOnlyList<string>> parents, string name)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(parents);
        var features = Index(inventory);
        if (!features.TryGetValue(name, out var requested))
        {
            return new FeatureChange([], [], [], [$"Feature '{name}' is not in this image."]);
        }

        if (requested.IsOff)
        {
            return new FeatureChange([], [], [], []);
        }

        var problems = new List<string>();
        var unread = inventory.Features.Where(f => !parents.ContainsKey(f.Name)).Select(f => f.Name).ToList();
        if (unread.Count > 0)
        {
            problems.Add($"The parents of {unread.Count} feature(s) are unknown, so what depends on '{requested.Name}' is unknown: {string.Join(", ", unread)}.");
        }

        var children = new Dictionary<string, List<WindowsFeature>>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in inventory.Features)
        {
            foreach (var parent in parents.GetValueOrDefault(feature.Name, []))
            {
                if (!children.TryGetValue(parent, out var list))
                {
                    children[parent] = list = [];
                }

                list.Add(feature);
            }
        }

        var order = new List<WindowsFeature>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(requested);
        return Result(order, requested, problems, needsSource: false);

        void Visit(WindowsFeature feature)
        {
            if (done.Contains(feature.Name))
            {
                return;
            }

            if (!visiting.Add(feature.Name))
            {
                problems.Add($"Feature '{feature.Name}' depends on itself.");
                return;
            }

            if (!feature.IsOn)
            {
                problems.Add($"Feature '{feature.Name}' is {feature.RawState}; it cannot be disabled now.");
            }

            foreach (var child in children.GetValueOrDefault(feature.Name, []))
            {
                if (!child.IsOff)
                {
                    Visit(child);
                }
            }

            visiting.Remove(feature.Name);
            done.Add(feature.Name);
            order.Add(feature);
        }
    }

    /// <summary>
    /// Every feature whose state differs between two lists of the same image. DISM also turns on default sub-features
    /// that no API reports beforehand (found in the VM: enabling IIS-WebServerRole enabled IIS-StaticContent and
    /// others), so a change is checked against the whole list, not only the features it named.
    /// </summary>
    public static IReadOnlyList<FeatureStateChange> Compare(FeatureInventory before, FeatureInventory after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var old = Index(before);
        var changes = new List<FeatureStateChange>();
        foreach (var feature in after.Features)
        {
            var was = old.GetValueOrDefault(feature.Name);
            if (was?.RawState != feature.RawState)
            {
                changes.Add(new FeatureStateChange(feature.Name, was?.RawState, feature.RawState));
            }
        }

        changes.AddRange(before.Features.Where(f => !after.Features.Any(a => string.Equals(a.Name, f.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(f => new FeatureStateChange(f.Name, f.RawState, null)));
        return changes;
    }

    private static Dictionary<string, WindowsFeature> Index(FeatureInventory inventory) =>
        inventory.Features.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    private static FeatureChange Result(List<WindowsFeature> order, WindowsFeature requested, List<string> problems, bool needsSource)
    {
        if (problems.Count > 0)
        {
            return new FeatureChange([], [], [], problems);
        }

        return new FeatureChange(
            [.. order.Select(f => f.Name)],
            [.. order.Where(f => f != requested).Select(f => f.Name)],
            needsSource ? [.. order.Where(f => f.State == FeatureState.DisabledWithPayloadRemoved).Select(f => f.Name)] : [],
            []);
    }
}
