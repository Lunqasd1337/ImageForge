using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace ImageForge.App;

/// <summary>
/// Window texts from Strings/Resources.resw (plan, section 14). Controls get theirs by x:Uid; code reads them here.
/// A missing text shows as [Name] instead of failing the window; MessagesTests checks that none is missing.
/// </summary>
internal static class Ui
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string name)
    {
        try
        {
            var text = Loader.GetString(name);
            return string.IsNullOrEmpty(text) ? $"[{name}]" : text;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return $"[{name}]";
        }
    }

    private static string Format(string name, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(name), args);

    public static string AnsiCodePageInvalid => Get(nameof(AnsiCodePageInvalid));
    public static string AppsCount(object? a0) => Format(nameof(AppsCount), a0);
    public static string AppsNotRead => Get(nameof(AppsNotRead));
    public static string AppxFramework => Get(nameof(AppxFramework));
    public static string AppxNeededBy(object? a0) => Format(nameof(AppxNeededBy), a0);
    public static string AvailableCount(object? a0, object? a1, object? a2) => Format(nameof(AvailableCount), a0, a1, a2);
    public static string CapabilitiesCount(object? a0) => Format(nameof(CapabilitiesCount), a0);
    public static string CapabilitiesNotRead => Get(nameof(CapabilitiesNotRead));
    public static string CapabilitiesNotReadReason => Get(nameof(CapabilitiesNotReadReason));
    public static string DriverBootCriticalNote => Get(nameof(DriverBootCriticalNote));
    public static string DriverCatalog => Get(nameof(DriverCatalog));
    public static string DriverFilesNotRead => Get(nameof(DriverFilesNotRead));
    public static string DriverFolderChanged => Get(nameof(DriverFolderChanged));
    public static string DriverFolderPrompt => Get(nameof(DriverFolderPrompt));
    public static string DriverNoCatalog => Get(nameof(DriverNoCatalog));
    public static string DriverNotInf => Get(nameof(DriverNotInf));
    public static string DriverPackagesCount(object? a0, object? a1, object? a2) => Format(nameof(DriverPackagesCount), a0, a1, a2);
    public static string DriverSameContent(object? a0) => Format(nameof(DriverSameContent), a0);
    public static string DriverUnsignedNote => Get(nameof(DriverUnsignedNote));
    public static string DriversNotRead => Get(nameof(DriversNotRead));
    public static string DriversSearching => Get(nameof(DriversSearching));
    public static string EditionChanged => Get(nameof(EditionChanged));
    public static string EditionChosen(object? a0, object? a1, object? a2) => Format(nameof(EditionChosen), a0, a1, a2);
    public static string EditionsBeforeInspect => Get(nameof(EditionsBeforeInspect));
    public static string EditionsNotRead(object? a0) => Format(nameof(EditionsNotRead), a0);
    public static string EngineConnected => Get(nameof(EngineConnected));
    public static string EngineDidNotStart => Get(nameof(EngineDidNotStart));
    public static string EngineError => Get(nameof(EngineError));
    public static string EngineIncompatible(object? a0) => Format(nameof(EngineIncompatible), a0);
    public static string EngineLost => Get(nameof(EngineLost));
    public static string EngineNotConnected => Get(nameof(EngineNotConnected));
    public static string EngineNotFound(object? a0) => Format(nameof(EngineNotFound), a0);
    public static string EnginePipeForeign => Get(nameof(EnginePipeForeign));
    public static string EngineRejected => Get(nameof(EngineRejected));
    public static string EngineStartFailed => Get(nameof(EngineStartFailed));
    public static string EngineStarting => Get(nameof(EngineStarting));
    public static string EngineUacCancelled => Get(nameof(EngineUacCancelled));
    public static string FeatureFromMedia => Get(nameof(FeatureFromMedia));
    public static string FeaturesBlocked => Get(nameof(FeaturesBlocked));
    public static string FeaturesCount(object? a0, object? a1) => Format(nameof(FeaturesCount), a0, a1);
    public static string FeaturesHint => Get(nameof(FeaturesHint));
    public static string FeaturesNotRead => Get(nameof(FeaturesNotRead));
    public static string FileGenitiveMany => Get(nameof(FileGenitiveMany));
    public static string FileGenitiveOne => Get(nameof(FileGenitiveOne));
    public static string FilePickerFailed => Get(nameof(FilePickerFailed));
    public static string FolderInvalid => Get(nameof(FolderInvalid));
    public static string FolderPickerFailed => Get(nameof(FolderPickerFailed));
    public static string FoldersChangedApps => Get(nameof(FoldersChangedApps));
    public static string FoldersChangedEditions => Get(nameof(FoldersChangedEditions));
    public static string FoldersInvalid => Get(nameof(FoldersInvalid));
    public static string ImageDriversCount(object? a0, object? a1) => Format(nameof(ImageDriversCount), a0, a1);
    public static string ImageDriversNone => Get(nameof(ImageDriversNone));
    public static string ImageDriversNotRead => Get(nameof(ImageDriversNotRead));
    public static string ImageNotRead => Get(nameof(ImageNotRead));
    public static string ImageRead => Get(nameof(ImageRead));
    public static string Inspecting => Get(nameof(Inspecting));
    public static string LastRun(object? a0, object? a1, object? a2) => Format(nameof(LastRun), a0, a1, a2);
    public static string LastRunStep(object? a0) => Format(nameof(LastRunStep), a0);
    public static string LocationNested(object? a0, object? a1) => Format(nameof(LocationNested), a0, a1);
    public static string LocationNotLocal(object? a0) => Format(nameof(LocationNotLocal), a0);
    public static string LocationOutputDir => Get(nameof(LocationOutputDir));
    public static string LocationSame(object? a0, object? a1) => Format(nameof(LocationSame), a0, a1);
    public static string LocationSource => Get(nameof(LocationSource));
    public static string LocationWorkDir => Get(nameof(LocationWorkDir));
    public static string LogDriverFolder(object? a0, object? a1, object? a2) => Format(nameof(LogDriverFolder), a0, a1, a2);
    public static string LogEngine(object? a0, object? a1, object? a2, object? a3) => Format(nameof(LogEngine), a0, a1, a2, a3);
    public static string LogOutcome(object? a0, object? a1, object? a2, object? a3, object? a4) => Format(nameof(LogOutcome), a0, a1, a2, a3, a4);
    public static string LogPlan(object? a0, object? a1, object? a2) => Format(nameof(LogPlan), a0, a1, a2);
    public static string LogRecovery => Get(nameof(LogRecovery));
    public static string LogRun(object? a0) => Format(nameof(LogRun), a0);
    public static string LogUpdateFolder(object? a0, object? a1, object? a2) => Format(nameof(LogUpdateFolder), a0, a1, a2);
    public static string LogWorkspace(object? a0, object? a1, object? a2, object? a3) => Format(nameof(LogWorkspace), a0, a1, a2, a3);
    public static string NoRuns => Get(nameof(NoRuns));
    public static string None => Get(nameof(None));
    public static string PackagesNotRead => Get(nameof(PackagesNotRead));
    public static string PackagesStatus(object? a0, object? a1, object? a2) => Format(nameof(PackagesStatus), a0, a1, a2);
    public static string PlanApps(object? a0) => Format(nameof(PlanApps), a0);
    public static string PlanBoot(object? a0) => Format(nameof(PlanBoot), a0);
    public static string PlanCapabilityAdditions(object? a0) => Format(nameof(PlanCapabilityAdditions), a0);
    public static string PlanCapabilityRemovals(object? a0) => Format(nameof(PlanCapabilityRemovals), a0);
    public static string PlanCleanup(object? a0) => Format(nameof(PlanCleanup), a0);
    public static string PlanDiagnostic(object? a0, object? a1) => Format(nameof(PlanDiagnostic), a0, a1);
    public static string PlanDrivers(object? a0, object? a1) => Format(nameof(PlanDrivers), a0, a1);
    public static string PlanFeatures(object? a0, object? a1) => Format(nameof(PlanFeatures), a0, a1);
    public static string PlanNotBuilt => Get(nameof(PlanNotBuilt));
    public static string PlanReady(object? a0, object? a1, object? a2, object? a3, object? a4, object? a5, object? a6) => Format(nameof(PlanReady), a0, a1, a2, a3, a4, a5, a6);
    public static string PlanResetBase => Get(nameof(PlanResetBase));
    public static string PlanUpdates(object? a0) => Format(nameof(PlanUpdates), a0);
    public static string PlanWinRe(object? a0) => Format(nameof(PlanWinRe), a0);
    public static string Present => Get(nameof(Present));
    public static string PreviewAppx(object? a0, object? a1) => Format(nameof(PreviewAppx), a0, a1);
    public static string PreviewBeforePrepare => Get(nameof(PreviewBeforePrepare));
    public static string PreviewBoot => Get(nameof(PreviewBoot));
    public static string PreviewBootAdd(object? a0) => Format(nameof(PreviewBootAdd), a0);
    public static string PreviewCapabilityAdd(object? a0) => Format(nameof(PreviewCapabilityAdd), a0);
    public static string PreviewCapabilityAddNamed(object? a0, object? a1) => Format(nameof(PreviewCapabilityAddNamed), a0, a1);
    public static string PreviewCapabilityDependencies => Get(nameof(PreviewCapabilityDependencies));
    public static string PreviewCapabilityRemove(object? a0) => Format(nameof(PreviewCapabilityRemove), a0);
    public static string PreviewCapabilityRemoveNamed(object? a0, object? a1) => Format(nameof(PreviewCapabilityRemoveNamed), a0, a1);
    public static string PreviewCleanup => Get(nameof(PreviewCleanup));
    public static string PreviewCleanupResetBase => Get(nameof(PreviewCleanupResetBase));
    public static string PreviewDriverAdd(object? a0, object? a1) => Format(nameof(PreviewDriverAdd), a0, a1);
    public static string PreviewDriverBootCritical => Get(nameof(PreviewDriverBootCritical));
    public static string PreviewDriverRemove(object? a0, object? a1) => Format(nameof(PreviewDriverRemove), a0, a1);
    public static string PreviewDriverSameContent(object? a0) => Format(nameof(PreviewDriverSameContent), a0);
    public static string PreviewDriverUnsigned => Get(nameof(PreviewDriverUnsigned));
    public static string PreviewFeatureAddedOff => Get(nameof(PreviewFeatureAddedOff));
    public static string PreviewFeatureAddedOn => Get(nameof(PreviewFeatureAddedOn));
    public static string PreviewFeatureDefaults => Get(nameof(PreviewFeatureDefaults));
    public static string PreviewFeatureDisable(object? a0, object? a1) => Format(nameof(PreviewFeatureDisable), a0, a1);
    public static string PreviewFeatureEnable(object? a0, object? a1) => Format(nameof(PreviewFeatureEnable), a0, a1);
    public static string PreviewNetFx3Source => Get(nameof(PreviewNetFx3Source));
    public static string PreviewSource(object? a0) => Format(nameof(PreviewSource), a0);
    public static string PreviewUpdateAdd(object? a0) => Format(nameof(PreviewUpdateAdd), a0);
    public static string PreviewUpdateDuration => Get(nameof(PreviewUpdateDuration));
    public static string PreviewUpdateSameContent(object? a0) => Format(nameof(PreviewUpdateSameContent), a0);
    public static string PreviewWinRe => Get(nameof(PreviewWinRe));
    public static string PreviewWinReAdd(object? a0) => Format(nameof(PreviewWinReAdd), a0);
    public static string Recovering => Get(nameof(Recovering));
    public static string RecoveryDone => Get(nameof(RecoveryDone));
    public static string RecoveryFailed => Get(nameof(RecoveryFailed));
    public static string RecoveryNeeded => Get(nameof(RecoveryNeeded));
    public static string RegFileEncoding(object? a0) => Format(nameof(RegFileEncoding), a0);
    public static string RegFileNotFound => Get(nameof(RegFileNotFound));
    public static string RegFileReadFailed => Get(nameof(RegFileReadFailed));
    public static string RegFileRejected(object? a0) => Format(nameof(RegFileRejected), a0);
    public static string RegFileTooLarge(object? a0) => Format(nameof(RegFileTooLarge), a0);
    public static string RunDone => Get(nameof(RunDone));
    public static string RunFailed(object? a0, object? a1, object? a2) => Format(nameof(RunFailed), a0, a1, a2);
    public static string RunRejected(object? a0) => Format(nameof(RunRejected), a0);
    public static string Running => Get(nameof(Running));
    public static string UpdateFileNotRead => Get(nameof(UpdateFileNotRead));
    public static string UpdateFilesBoot(object? a0) => Format(nameof(UpdateFilesBoot), a0);
    public static string UpdateFilesFound(object? a0, object? a1) => Format(nameof(UpdateFilesFound), a0, a1);
    public static string UpdateFilesTargets(object? a0, object? a1) => Format(nameof(UpdateFilesTargets), a0, a1);
    public static string UpdateFolderChanged => Get(nameof(UpdateFolderChanged));
    public static string UpdateFolderPrompt => Get(nameof(UpdateFolderPrompt));
    public static string UpdateNoKb => Get(nameof(UpdateNoKb));
    public static string UpdateSameContent(object? a0, object? a1) => Format(nameof(UpdateSameContent), a0, a1);
    public static string UpdateServicingStack => Get(nameof(UpdateServicingStack));
    public static string UpdateSha1InName(object? a0) => Format(nameof(UpdateSha1InName), a0);
    public static string UpdatesHashing => Get(nameof(UpdatesHashing));
    public static string UpdatesNotRead => Get(nameof(UpdatesNotRead));
    public static string Warning => Get(nameof(Warning));
}
