using CommunityToolkit.Mvvm.ComponentModel;
using ImageForge.Core.Workspace;

namespace ImageForge.App;

/// <summary>One installed capability of the inspected image, as a row the user can mark for removal.</summary>
public sealed partial class CapabilityItem(CapabilityView capability, Action selectionChanged) : ObservableObject
{
    public CapabilityView Capability { get; } = capability;

    public string Title => Capability.DisplayName;

    public string Details => Capability.Name;

    // bool? because CheckBox.IsChecked is nullable; null is never set here.
    [ObservableProperty]
    public partial bool? IsSelected { get; set; } = false;

    partial void OnIsSelectedChanged(bool? value) => selectionChanged();
}
