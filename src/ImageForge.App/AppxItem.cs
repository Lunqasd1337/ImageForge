using CommunityToolkit.Mvvm.ComponentModel;
using ImageForge.Core.Workspace;

namespace ImageForge.App;

/// <summary>One provisioned package of the inspected image, as a row the user can mark for removal.</summary>
public sealed partial class AppxItem(AppxPackageView package, Action selectionChanged) : ObservableObject
{
    public AppxPackageView Package { get; } = package;

    public string Title => Package.DisplayName;

    public string Details => $"{Package.Version} · {Package.Architecture} · {Package.FamilyName}{(Package.IsFramework ? " · " + Ui.AppxFramework : string.Empty)}";

    /// <summary>Why removal is blocked, which apps need this one, and the warning; empty when there is nothing to say.</summary>
    public string Note => Package.BlockedReason ?? string.Join(
        Environment.NewLine,
        new[]
        {
            Package.NeededBy.Count > 0 ? Ui.AppxNeededBy(string.Join(", ", Package.NeededBy)) : null,
            Package.Warning is { } warning ? (Ui.Warning + " ") + warning : null,
        }.OfType<string>());

    public bool HasNote => Note.Length > 0;

    public bool CanSelect => Package.BlockedReason is null;

    // bool? because CheckBox.IsChecked is nullable; null is never set here.
    [ObservableProperty]
    public partial bool? IsSelected { get; set; } = false;

    partial void OnIsSelectedChanged(bool? value) => selectionChanged();
}
