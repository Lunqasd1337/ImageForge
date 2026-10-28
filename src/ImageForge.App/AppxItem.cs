using CommunityToolkit.Mvvm.ComponentModel;
using ImageForge.Core.Workspace;

namespace ImageForge.App;

/// <summary>One provisioned package of the inspected image, as a row the user can mark for removal.</summary>
public sealed partial class AppxItem(AppxPackageView package, Action selectionChanged) : ObservableObject
{
    public AppxPackageView Package { get; } = package;

    public string Title => Package.DisplayName;

    public string Details => $"{Package.Version} · {Package.Architecture} · {Package.FamilyName}";

    /// <summary>The warning or the reason removal is blocked; empty when there is neither.</summary>
    public string Note => Package.BlockedReason ?? (Package.Warning is { } warning ? "Внимание: " + warning : string.Empty);

    public bool HasNote => Note.Length > 0;

    public bool CanSelect => Package.BlockedReason is null;

    // bool? because CheckBox.IsChecked is nullable; null is never set here.
    [ObservableProperty]
    public partial bool? IsSelected { get; set; } = false;

    partial void OnIsSelectedChanged(bool? value) => selectionChanged();
}
