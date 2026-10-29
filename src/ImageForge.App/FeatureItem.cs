using CommunityToolkit.Mvvm.ComponentModel;
using ImageForge.Core.Workspace;

namespace ImageForge.App;

/// <summary>One Windows feature of the inspected image; the check box is the state the user wants after the run.</summary>
public sealed partial class FeatureItem : ObservableObject
{
    private readonly Action _selectionChanged;

    public FeatureItem(FeatureView feature, Action selectionChanged)
    {
        Feature = feature;
        _selectionChanged = selectionChanged;
        IsSelected = feature.IsOn;
    }

    public FeatureView Feature { get; }

    public string Title => Feature.DisplayName;

    public string Details => $"{Feature.Name} · {Feature.State}{(Feature.FromMedia ? " · файлы будут взяты из sources\\sxs дистрибутива" : string.Empty)}";

    public string Note => Feature.BlockedReason ?? string.Empty;

    public bool HasNote => Note.Length > 0;

    public bool CanSelect => Feature.BlockedReason is null;

    /// <summary>The user wants it turned on while it is off now.</summary>
    public bool TurnsOn => IsSelected == true && !Feature.IsOn;

    /// <summary>The user wants it turned off while it is on now.</summary>
    public bool TurnsOff => IsSelected == false && Feature.IsOn;

    // bool? because CheckBox.IsChecked is nullable; null is never set here.
    [ObservableProperty]
    public partial bool? IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool? value) => _selectionChanged();
}
