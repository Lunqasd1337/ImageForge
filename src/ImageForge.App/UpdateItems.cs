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
            return $"{p.Kind} · {(p.Kb is { } kb ? "KB" + kb : Ui.UpdateNoKb)} · {p.Architecture ?? "-"} · {container}"
                + (p.HasServicingStack ? " · " + Ui.UpdateServicingStack : string.Empty)
                + (p.IsSetupDynamicUpdate ? " · Setup DU" : string.Empty) + " · " + Ui.UpdateSha1InName(p.Sha1FromName is null ? Ui.None : Ui.Present);
        }
    }

    public string Note => Scanned.Package.Problems.Count > 0 ? string.Join(" ", Scanned.Package.Problems)
        : Scanned.Sha256 is null ? Ui.UpdateFileNotRead
        : sameContent > 1 ? Ui.UpdateSameContent(sameContent - 1, Files(sameContent - 1))
        : Scanned.Package.IsSetupDynamicUpdate ? Ui.UpdateSetupDuNote
        : string.Empty;

    public bool HasNote => Note.Length > 0;

    // Genitive after «у»: у 1 файла, у 21 файла, у 2 файлов, у 11 файлов.
    private static string Files(int n) => n % 10 == 1 && n % 100 != 11 ? Ui.FileGenitiveOne : Ui.FileGenitiveMany;

    private bool IsUsable => Scanned.Package.Problems.Count == 0 && Scanned.Sha256 is not null;

    /// <summary>Into an image: every usable package except a Setup Dynamic Update, which is not a DISM package.</summary>
    public bool CanSelect => IsUsable && !Scanned.Package.IsSetupDynamicUpdate;

    /// <summary>Onto the media: only a Setup Dynamic Update.</summary>
    public bool CanSelectMedia => IsUsable && Scanned.Package.IsSetupDynamicUpdate;

    // bool? because CheckBox.IsChecked is nullable; null is never set here.
    [ObservableProperty]
    public partial bool? IsSelected { get; set; } = false;

    partial void OnIsSelectedChanged(bool? value) => selectionChanged();

    /// <summary>Also (or only) into WinRE: the cumulative update for its servicing stack, the Safe OS Dynamic Update.</summary>
    [ObservableProperty]
    public partial bool? IsForWinRe { get; set; } = false;

    partial void OnIsForWinReChanged(bool? value) => selectionChanged();

    /// <summary>Into every image of boot.wim: the cumulative update (or a servicing stack).</summary>
    [ObservableProperty]
    public partial bool? IsForBoot { get; set; } = false;

    partial void OnIsForBootChanged(bool? value) => selectionChanged();

    /// <summary>Expanded into the media's sources: the Setup Dynamic Update.</summary>
    [ObservableProperty]
    public partial bool? IsForMedia { get; set; } = false;

    partial void OnIsForMediaChanged(bool? value) => selectionChanged();
}
