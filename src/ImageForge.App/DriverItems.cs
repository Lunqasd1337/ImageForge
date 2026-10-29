using CommunityToolkit.Mvvm.ComponentModel;
using ImageForge.Core.Drivers;

namespace ImageForge.App;

/// <summary>One third-party driver of the inspected image, as a row the user can mark for removal.</summary>
public sealed partial class InstalledDriverItem(InstalledDriver driver, Action selectionChanged) : ObservableObject
{
    public InstalledDriver Driver { get; } = driver;

    public string Title => $"{Driver.PublishedName} — {Driver.InfName}";

    public string Details => $"{Driver.ClassName} · {Driver.Provider} · {Driver.Date:yyyy-MM-dd} {Driver.Version} · {Driver.Signature}";

    public string Note => Driver.BootCritical ? "Нужен для загрузки: удалить можно только с разрешением ниже." : string.Empty;

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
          $"{(inf.CatalogFile is { } cat ? "каталог " + cat : "без подписи")} · {(inf.Architectures.Count == 0 ? "-" : string.Join(", ", inf.Architectures))}"
        : "не INF драйвера";

    public string Note => Scanned.Package.Problems.Count > 0 ? string.Join(" ", Scanned.Package.Problems)
        : Scanned.ContentHash is null ? "Файлы пакета не прочитаны."
        : Scanned.Package.IsUnsigned ? "Без подписи: добавляется только с ForceUnsigned (разрешение ниже)."
        : sameContent > 1 ? $"Такое же содержимое ещё у {sameContent - 1} INF в этой папке; отмеченные копии добавятся один раз."
        : string.Empty;

    public bool HasNote => Note.Length > 0;

    public bool CanSelect => Scanned.Package.Inf is not null && Scanned.Package.Problems.Count == 0 && Scanned.ContentHash is not null;

    // bool? because CheckBox.IsChecked is nullable; null is never set here.
    [ObservableProperty]
    public partial bool? IsSelected { get; set; } = false;

    partial void OnIsSelectedChanged(bool? value) => selectionChanged();
}
