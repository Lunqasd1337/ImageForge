using CommunityToolkit.Mvvm.ComponentModel;
using ImageForge.Core.Updates;

namespace ImageForge.App;

/// <summary>One .msu or .cab found in the update folder; a package with problems cannot be marked.</summary>
/// <param name="sameContent">How many packages of the folder have this content, this one included.</param>
public sealed partial class UpdatePackageItem(ScannedUpdate scanned, int sameContent, Action selectionChanged) : ObservableObject
{
    public ScannedUpdate Scanned { get; } = scanned;

    public string Title => Scanned.Package.FileName;

    public string Details
    {
        get
        {
            var p = Scanned.Package;
            var container = p.Container switch
            {
                UpdateContainer.Wim => "WIM",
                UpdateContainer.Cabinet => "cabinet",
                _ => "-",
            };
            return $"{p.Kind} · {(p.Kb is { } kb ? "KB" + kb : "без KB")} · {p.Architecture ?? "-"} · {container}"
                + $"{(p.HasServicingStack ? " · стек обслуживания" : string.Empty)} · SHA-1 в имени {(p.Sha1FromName is null ? "нет" : "есть")}";
        }
    }

    public string Note => Scanned.Package.Problems.Count > 0 ? string.Join(" ", Scanned.Package.Problems)
        : Scanned.Sha256 is null ? "Файл не прочитан."
        : sameContent > 1 ? $"Такое же содержимое ещё у {sameContent - 1} {Files(sameContent - 1)} в этой папке; отмеченные копии поставятся один раз."
        : string.Empty;

    public bool HasNote => Note.Length > 0;

    // Genitive after «у»: у 1 файла, у 21 файла, у 2 файлов, у 11 файлов.
    private static string Files(int n) => n % 10 == 1 && n % 100 != 11 ? "файла" : "файлов";

    public bool CanSelect => Scanned.Package.Problems.Count == 0 && Scanned.Sha256 is not null;

    // bool? because CheckBox.IsChecked is nullable; null is never set here.
    [ObservableProperty]
    public partial bool? IsSelected { get; set; } = false;

    partial void OnIsSelectedChanged(bool? value) => selectionChanged();

    /// <summary>Also (or only) into WinRE: the cumulative update for its servicing stack, the Safe OS Dynamic Update.</summary>
    [ObservableProperty]
    public partial bool? IsForWinRe { get; set; } = false;

    partial void OnIsForWinReChanged(bool? value) => selectionChanged();
}
