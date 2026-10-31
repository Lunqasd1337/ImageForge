using CommunityToolkit.Mvvm.ComponentModel;
using ImageForge.Core.Drivers;

namespace ImageForge.App;

/// <summary>One third-party driver of the inspected image, as a row the user can mark for removal.</summary>
public sealed partial class InstalledDriverItem(InstalledDriver driver, Action selectionChanged) : ObservableObject
{
    public InstalledDriver Driver { get; } = driver;

    public string Title => $"{Driver.PublishedName} — {Driver.InfName}";

    public string Details => $"{Driver.ClassName} · {Driver.Provider} · {Driver.Date:yyyy-MM-dd} {Driver.Version} · {Driver.Signature}";

    public string Note => Driver.BootCritical ? Ui.DriverBootCriticalNote : string.Empty;

    public bool HasNote => Note.Length > 0;

    // bool? because CheckBox.IsChecked is nullable; null is never set here.
    [ObservableProperty]
    public partial bool? IsSelected { get; set; } = false;

    partial void OnIsSelectedChanged(bool? value) => selectionChanged();
}

/// <summary>One INF found in the driver folder; a package with problems cannot be marked.</summary>
/// <param name="sameContent">How many packages of the folder have this content, this one included.</param>
public sealed partial class DriverPackageItem(ScannedDriver scanned, int sameContent, Action selectionChanged) : ObservableObject
{
    public ScannedDriver Scanned { get; } = scanned;

    public string Title => Scanned.Package.InfPath;

    public string Details => Scanned.Package.Inf is { } inf
        ? $"{inf.Class ?? "-"} · {inf.Provider ?? "-"} · {inf.DriverDate ?? "-"} {inf.DriverVersion ?? "-"} · " +
          $"{(inf.CatalogFile is { } cat ? (Ui.DriverCatalog + " ") + cat : Ui.DriverNoCatalog)} · {(inf.Architectures.Count == 0 ? "-" : string.Join(", ", inf.Architectures))}"
        : Ui.DriverNotInf;

    public string Note => Scanned.Package.Problems.Count > 0 ? string.Join(" ", Scanned.Package.Problems)
        : Scanned.ContentHash is null ? Ui.DriverFilesNotRead
        : Scanned.Package.IsUnsigned ? Ui.DriverUnsignedNote
        : sameContent > 1 ? Ui.DriverSameContent(sameContent - 1)
        : string.Empty;

    public bool HasNote => Note.Length > 0;

    public bool CanSelect => Scanned.Package.Inf is not null && Scanned.Package.Problems.Count == 0 && Scanned.ContentHash is not null;

    // bool? because CheckBox.IsChecked is nullable; null is never set here.
    [ObservableProperty]
    public partial bool? IsSelected { get; set; } = false;

    partial void OnIsSelectedChanged(bool? value) => selectionChanged();

    /// <summary>Also (or only) into WinRE: storage drivers the recovery environment needs to see the disk.</summary>
    [ObservableProperty]
    public partial bool? IsForWinRe { get; set; } = false;

    partial void OnIsForWinReChanged(bool? value) => selectionChanged();

    /// <summary>Into every image of boot.wim: storage and network drivers Windows Setup needs.</summary>
    [ObservableProperty]
    public partial bool? IsForBoot { get; set; } = false;

    partial void OnIsForBootChanged(bool? value) => selectionChanged();
}
