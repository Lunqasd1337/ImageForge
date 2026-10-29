namespace ImageForge.Core.Features;

/// <param name="Features">Every feature to pass to DISM, in order: for an enable, parents before the features that
/// need them; for a disable, dependents before the features they need. Empty when nothing needs to change.</param>
/// <param name="Added">The features in <see cref="Features"/> that the user did not ask for, to show before the change.</param>
/// <param name="NeedsSource">Features in <see cref="Features"/> whose payload was removed from the image.</param>
/// <param name="Problems">Anything that stops the change; when not empty, nothing may be changed.</param>
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

        if (requested.State == FeatureState.Enabled)
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

            if (feature.State is FeatureState.Unknown or FeatureState.Pending)
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
                else if (parent.State != FeatureState.Enabled)
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

        if (requested.State is FeatureState.Disabled or FeatureState.DisabledWithPayloadRemoved)
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

            if (feature.State is FeatureState.Unknown or FeatureState.Pending)
            {
                problems.Add($"Feature '{feature.Name}' is {feature.RawState}; it cannot be disabled now.");
            }

            foreach (var child in children.GetValueOrDefault(feature.Name, []))
            {
                if (child.State is not (FeatureState.Disabled or FeatureState.DisabledWithPayloadRemoved))
                {
                    Visit(child);
                }
            }

            visiting.Remove(feature.Name);
            done.Add(feature.Name);
            order.Add(feature);
        }
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
