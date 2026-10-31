using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageForge.Core.Contracts;
using ImageForge.Core.Drivers;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry.RegFile;
using ImageForge.Core.Updates;
using ImageForge.Core.Workspace;
using Microsoft.UI.Dispatching;
using StreamJsonRpc;

namespace ImageForge.App;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private EngineClient? _engine;
    private PlanSummary? _plan;

    // The folders the engine workspace was opened with; reopening forgets the inventory, so it happens only on change.
    private OpenWorkspaceRequest? _opened;

    // The editions of the opened source, in the order of EditionNames; empty until the image is read.
    private IReadOnlyList<ImageEdition> _editions = [];
    private bool _showingEditions;

    [ObservableProperty]
    public partial string SourcePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WorkPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OutputPath { get; set; } = string.Empty;

    /// <summary>The install.wim editions shown after the image is read; only the chosen one is changed.</summary>
    public ObservableCollection<string> EditionNames { get; } = [];

    [ObservableProperty]
    public partial int EditionIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string EditionStatus { get; set; } = Ui.EditionsBeforeInspect;

    /// <summary>The .reg file to apply; empty plans the M1 validation marker.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRegFile))]
    public partial string RegFilePath { get; set; } = string.Empty;

    /// <summary>Index into Авто, UTF-16, UTF-8, ANSI; the order of <see cref="RegFileEncoding"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnsi))]
    public partial int RegFileEncodingIndex { get; set; }

    [ObservableProperty]
    public partial string AnsiCodePage { get; set; } = "1251";

    [ObservableProperty]
    public partial string Status { get; set; } = Ui.EngineNotConnected;

    [ObservableProperty]
    public partial string Log { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OperationPreview { get; set; } = Ui.PreviewBeforePrepare;

    [ObservableProperty]
    public partial string PublishedPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyCanExecuteChangedFor(nameof(ConnectEngineCommand), nameof(PrepareCommand), nameof(StartCommand), nameof(RecoverCommand), nameof(InspectCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool HasPlan { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecoverCommand))]
    public partial bool RecoveryRequired { get; set; }

    /// <summary>Provisioned packages of the last inspection; empty until «Прочитать образ».</summary>
    public ObservableCollection<AppxItem> Apps { get; } = [];

    [ObservableProperty]
    public partial string AppsStatus { get; set; } = Ui.AppsNotRead;

    /// <summary>Windows features of the last inspection; the check box is the wanted state.</summary>
    public ObservableCollection<FeatureItem> Features { get; } = [];

    [ObservableProperty]
    public partial string FeaturesStatus { get; set; } = Ui.FeaturesNotRead;

    /// <summary>Installed capabilities of the last inspection; a check marks one for removal.</summary>
    public ObservableCollection<CapabilityItem> Capabilities { get; } = [];

    [ObservableProperty]
    public partial string CapabilitiesStatus { get; set; } = Ui.CapabilitiesNotRead;

    // Every capability that can be added; AvailableCapabilities shows those matching the filter, marks are kept here.
    private readonly List<CapabilityItem> _available = [];

    /// <summary>Not present capabilities matching <see cref="AvailableFilter"/>; a check marks one for adding.</summary>
    public ObservableCollection<CapabilityItem> AvailableCapabilities { get; } = [];

    [ObservableProperty]
    public partial string AvailableFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AvailableStatus { get; set; } = string.Empty;

    /// <summary>The LanguagesAndOptionalFeatures folder of the Features on Demand ISO; needed only for additions.</summary>
    [ObservableProperty]
    public partial string FodPath { get; set; } = string.Empty;

    /// <summary>Third-party drivers of the last inspection; a check marks one for removal.</summary>
    public ObservableCollection<InstalledDriverItem> ImageDrivers { get; } = [];

    [ObservableProperty]
    public partial string DriversStatus { get; set; } = Ui.DriversNotRead;

    /// <summary>A folder of driver packages, searched recursively; needed only for additions.</summary>
    [ObservableProperty]
    public partial string DriverPath { get; set; } = string.Empty;

    /// <summary>INF files found in <see cref="DriverPath"/>; a check marks one for adding.</summary>
    public ObservableCollection<DriverPackageItem> DriverPackages { get; } = [];

    [ObservableProperty]
    public partial string DriverPackagesStatus { get; set; } = Ui.DriverFolderPrompt;

    /// <summary>The user accepts adding packages without a catalog (DISM ForceUnsigned).</summary>
    [ObservableProperty]
    public partial bool ForceUnsigned { get; set; }

    /// <summary>The user accepts removing drivers that the image needs to boot.</summary>
    [ObservableProperty]
    public partial bool AllowBootCritical { get; set; }

    /// <summary>Features turned off also lose their files (/Remove); off by default (plan, section 10).</summary>
    [ObservableProperty]
    public partial bool RemoveFeaturePayload { get; set; }

    // The folder DriverPackages was found in; the engine scans it again when planning.
    private string? _scannedDriverFolder;

    [ObservableProperty]
    public partial string UpdatesStatus { get; set; } = Ui.UpdatesNotRead;

    /// <summary>A folder of .msu and .cab updates, searched recursively; needed only for additions.</summary>
    [ObservableProperty]
    public partial string UpdatePath { get; set; } = string.Empty;

    /// <summary>Update packages found in <see cref="UpdatePath"/>; a check marks one for adding.</summary>
    public ObservableCollection<UpdatePackageItem> UpdatePackages { get; } = [];

    [ObservableProperty]
    public partial string UpdatePackagesStatus { get; set; } = Ui.UpdateFolderPrompt;

    // The folder UpdatePackages was found in; the engine scans and hashes it again when planning.
    private string? _scannedUpdateFolder;

    /// <summary>Clean up the component store after the other servicing.</summary>
    [ObservableProperty]
    public partial bool CleanupComponents { get; set; }

    /// <summary>With the cleanup, also /ResetBase: superseded versions go for good.</summary>
    [ObservableProperty]
    public partial bool ResetBase { get; set; }

    public bool IsConnected => _engine is not null;

    public bool HasResult => PublishedPath.Length > 0;

    public bool HasRegFile => RegFilePath.Length > 0;

    public bool IsAnsi => RegFileEncodingIndex == (int)RegFileEncoding.Ansi;

    partial void OnPublishedPathChanged(string value) => OnPropertyChanged(nameof(HasResult));

    // Any change of the folders invalidates the shown plan and the inventory; they must be read again.
    partial void OnSourcePathChanged(string value) => FoldersChanged();

    partial void OnWorkPathChanged(string value) => FoldersChanged();

    partial void OnOutputPathChanged(string value) => FoldersChanged();

    // Another edition is another image: the inventory and the plan belong to the previous one.
    partial void OnEditionIndexChanged(int value)
    {
        if (!_showingEditions)
        {
            FoldersChanged(keepEditions: true);
            EditionStatus = Ui.EditionChanged;
        }
    }

    partial void OnRegFilePathChanged(string value) => DropPlan();

    partial void OnRegFileEncodingIndexChanged(int value) => DropPlan();

    partial void OnAnsiCodePageChanged(string value) => DropPlan();

    partial void OnFodPathChanged(string value) => DropPlan();

    partial void OnAvailableFilterChanged(string value) => ShowAvailable();

    partial void OnDriverPathChanged(string value)
    {
        DropPlan();
        DriverPackages.Clear();
        _scannedDriverFolder = null;
        DriverPackagesStatus = Ui.DriverFolderChanged;
    }

    partial void OnUpdatePathChanged(string value)
    {
        DropPlan();
        UpdatePackages.Clear();
        _scannedUpdateFolder = null;
        UpdatePackagesStatus = Ui.UpdateFolderChanged;
    }

    partial void OnForceUnsignedChanged(bool value) => DropPlan();

    partial void OnCleanupComponentsChanged(bool value)
    {
        DropPlan();
        if (!value)
        {
            ResetBase = false;
        }
    }

    partial void OnResetBaseChanged(bool value) => DropPlan();

    partial void OnAllowBootCriticalChanged(bool value) => DropPlan();

    partial void OnRemoveFeaturePayloadChanged(bool value) => DropPlan();

    /// <summary>Searches the folder for INF files here, unelevated; the engine checks the chosen packages again.</summary>
    /// <summary>Reads and hashes the .msu and .cab files here, unelevated; the engine checks the chosen ones again.</summary>
    [RelayCommand]
    private async Task ScanUpdatesAsync()
    {
        DropPlan();
        UpdatePackages.Clear();
        _scannedUpdateFolder = null;
        var folder = UpdatePath.Trim().Trim('"');
        UpdatePackagesStatus = Ui.UpdatesHashing;
        var scanned = await Task.Run(() => UpdateFolder.Scan(folder)).ConfigureAwait(true);
        if (scanned.Problem is { } problem)
        {
            UpdatePackagesStatus = (Ui.FolderInvalid + " ") + problem;
            return;
        }

        var copies = scanned.Packages.Where(p => p.Sha256 is not null).GroupBy(p => p.Sha256!).ToDictionary(g => g.Key, g => g.Count());
        foreach (var package in scanned.Packages)
        {
            UpdatePackages.Add(new UpdatePackageItem(package, package.Sha256 is { } hash ? copies[hash] : 1, UpdateSelectionChanged));
        }

        _scannedUpdateFolder = folder;
        Append(Ui.LogUpdateFolder(folder, scanned.Packages.Count, UpdatePackages.Count(p => p.CanSelect)));
        ShowUpdatePackagesStatus();
    }

    [RelayCommand]
    private async Task ScanDriversAsync()
    {
        DropPlan();
        DriverPackages.Clear();
        _scannedDriverFolder = null;
        var folder = DriverPath.Trim().Trim('"');
        DriverPackagesStatus = Ui.DriversSearching;
        var scanned = await Task.Run(() => DriverFolder.Scan(folder)).ConfigureAwait(true);
        if (scanned.Problem is { } problem)
        {
            DriverPackagesStatus = (Ui.FolderInvalid + " ") + problem;
            return;
        }

        var copies = scanned.Packages.Where(p => p.ContentHash is not null).GroupBy(p => p.ContentHash!).ToDictionary(g => g.Key, g => g.Count());
        foreach (var package in scanned.Packages)
        {
            DriverPackages.Add(new DriverPackageItem(package, package.ContentHash is { } hash ? copies[hash] : 1, DriverSelectionChanged));
        }

        _scannedDriverFolder = folder;
        Append(Ui.LogDriverFolder(folder, scanned.Packages.Count, DriverPackages.Count(p => p.CanSelect)));
        ShowDriverPackagesStatus();
    }

    [RelayCommand]
    private void ClearRegFile() => RegFilePath = string.Empty;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectEngineAsync(CancellationToken ct)
    {
        Status = Ui.EngineStarting;
        IsBusy = true;
        try
        {
            _engine = await EngineClient.StartAsync(ct).ConfigureAwait(true);
            var info = _engine.Info;
            Status = Ui.EngineConnected;
            Append(Ui.LogEngine(info.EngineVersion, info.ProtocolVersion, info.ProcessId, info.InstanceId));
        }
        catch (EngineLaunchException ex)
        {
            Status = ex.Message;
            Append(ex.ToString());
        }
        catch (Win32Exception ex)
        {
            Status = Ui.EngineStartFailed;
            Append(ex.ToString());
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsConnected));
            PrepareCommand.NotifyCanExecuteChanged();
            InspectCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanConnect() => !IsBusy && _engine is null;

    [RelayCommand(CanExecute = nameof(CanPrepare))]
    private async Task InspectAsync(CancellationToken ct)
    {
        DropPlan();
        ClearInventory();
        IsBusy = true;
        try
        {
            if (!await EnsureWorkspaceAsync(ct).ConfigureAwait(true))
            {
                return;
            }

            Status = Ui.Inspecting;
            var inspection = await _engine!.Service.InspectAsync(new UiProgress(this), ct).ConfigureAwait(true);
            if (inspection.Problems.Count > 0)
            {
                AppsStatus = (Ui.ImageNotRead + " ") + string.Join("; ", inspection.Problems);
                Status = AppsStatus;
                ShowWorkspace(await _engine.Service.GetWorkspaceAsync(ct).ConfigureAwait(true));
                return;
            }

            foreach (var package in inspection.Packages)
            {
                Apps.Add(new AppxItem(package, DropPlan));
            }

            AppsStatus = Ui.AppsCount(inspection.Packages.Count);
            ShowFeatures(inspection);
            ShowCapabilities(inspection);
            ShowDrivers(inspection);
            ShowUpdates(inspection);
            Status = Ui.ImageRead;
        }
        catch (RemoteInvocationException ex)
        {
            Status = (Ui.EngineRejected + " ") + ex.Message;
            Append(ex.ToString());
        }
        catch (ConnectionLostException ex)
        {
            Disconnected(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens the engine workspace for the current folders unless it is already open for them.</summary>
    private async Task<bool> EnsureWorkspaceAsync(CancellationToken ct)
    {
        var request = new OpenWorkspaceRequest(SourcePath.Trim(), WorkPath.Trim(), OutputPath.Trim())
        {
            ImageIndex = EditionIndex >= 0 && EditionIndex < _editions.Count ? _editions[EditionIndex].Index : 0,
        };
        var workspace = request == _opened
            ? await _engine!.Service.GetWorkspaceAsync(ct).ConfigureAwait(true)
            : await _engine!.Service.OpenWorkspaceAsync(request, ct).ConfigureAwait(true);
        if (workspace.Problems.Count > 0)
        {
            _opened = null;
            Status = (Ui.FoldersInvalid + " ") + string.Join("; ", workspace.Problems.Select(Describe));
            return false;
        }

        ShowEditions(workspace);
        _opened = request with { ImageIndex = workspace.Editions.Count > 0 ? workspace.ImageIndex : 0 };
        ShowWorkspace(workspace);
        if (workspace.RecoveryRequired)
        {
            Status = Ui.RecoveryNeeded;
            return false;
        }

        return true;
    }

    [RelayCommand(CanExecute = nameof(CanPrepare))]
    private async Task PrepareAsync(CancellationToken ct)
    {
        DropPlan();
        if (!TryReadRegFile(out var options))
        {
            return;
        }

        options = options with
        {
            RemoveAppx = [.. Apps.Where(a => a.IsSelected == true).Select(a => a.Package.FamilyName).Distinct()],
            EnableFeatures = [.. Features.Where(f => f.TurnsOn).Select(f => f.Feature.Name)],
            DisableFeatures = [.. Features.Where(f => f.TurnsOff).Select(f => f.Feature.Name)],
            RemoveCapabilities = [.. Capabilities.Where(c => c.IsSelected == true).Select(c => c.Capability.Name)],
            AddCapabilities = [.. _available.Where(c => c.IsSelected == true).Select(c => c.Capability.Name)],
            FodSource = FodPath.Trim().Trim('"') is { Length: > 0 } fod ? fod : null,
            RemoveDrivers = [.. ImageDrivers.Where(d => d.IsSelected == true).Select(d => d.Driver.PublishedName)],
            AllowBootCritical = AllowBootCritical,
            RemoveFeaturePayload = RemoveFeaturePayload,
            AddDrivers = [.. DriverPackages.Where(d => d.IsSelected == true).Select(d => d.Scanned.Package.InfPath)],
            DriverFolder = _scannedDriverFolder,
            ForceUnsigned = ForceUnsigned,
            AddUpdates = [.. UpdatePackages.Where(u => u.IsSelected == true).Select(u => u.Scanned.Package.FileName)],
            WinReUpdates = [.. UpdatePackages.Where(u => u.IsForWinRe == true).Select(u => u.Scanned.Package.FileName)],
            BootUpdates = [.. UpdatePackages.Where(u => u.IsForBoot == true).Select(u => u.Scanned.Package.FileName)],
            UpdateFolder = _scannedUpdateFolder,
            CleanupComponents = CleanupComponents,
            ResetBase = CleanupComponents && ResetBase,
        };
        IsBusy = true;
        try
        {
            if (!await EnsureWorkspaceAsync(ct).ConfigureAwait(true))
            {
                return;
            }

            var plan = await _engine!.Service.PlanAsync(options, ct).ConfigureAwait(true);
            var encoding = plan.FileEncoding is null ? string.Empty : (" " + Ui.RegFileEncoding(plan.FileEncoding));
            if (plan.FileDiagnostics.Count > 0)
            {
                Status = Ui.RegFileRejected(encoding);
                OperationPreview = string.Join(Environment.NewLine, plan.FileDiagnostics.Select(RegFileDiagnosticText.Describe));
                return;
            }

            if (plan.Diagnostics.Count > 0)
            {
                Status = Ui.PlanNotBuilt;
                OperationPreview = string.Join(Environment.NewLine, plan.Diagnostics.Select(d => Ui.PlanDiagnostic(d.ChangeIndex + 1, d.Message)));
                return;
            }

            _plan = plan;
            HasPlan = true;
            string[] lines =
            [
                // The order of the run (plan, section 11).
                .. plan.CapabilityRemovals.Select(DescribeCapabilityRemoval),
                .. DescribeCapabilityAdditions(plan),
                .. DescribeFeatureChanges(plan.FeatureChanges),
                .. DescribeUpdates(plan.Updates),
                .. DescribeDrivers(plan.Drivers),
                .. plan.AppxRemovals.Select(DescribeRemoval),
                .. DescribeCleanup(plan.Cleanup),
                .. DescribeDotNet(plan.DotNetUpdates),
                .. DescribeWinRe(plan.WinRe),
                .. DescribeBoot(plan.Boot),
                .. plan.Preview.Select(RegistryPreviewText.Describe),
            ];
            OperationPreview = string.Join(Environment.NewLine, lines);
            var apps = plan.AppxRemovals.Count > 0 ? (" " + Ui.PlanApps(plan.AppxRemovals.Count)) : string.Empty;
            var capabilities = (plan.CapabilityRemovals.Count > 0 ? (" " + Ui.PlanCapabilityRemovals(plan.CapabilityRemovals.Count)) : string.Empty)
                + (plan.CapabilityAdditions.Count > 0 ? (" " + Ui.PlanCapabilityAdditions(plan.CapabilityAdditions.Count)) : string.Empty);
            var features = plan.FeatureChanges is { } f ? (" " + Ui.PlanFeatures(f.Enable.Count, f.Disable.Count)) : string.Empty;
            var drivers = plan.Drivers is { } driverPlan ? (" " + Ui.PlanDrivers(driverPlan.Remove.Count, driverPlan.Add.Count)) : string.Empty;
            var updates = ((plan.Updates?.Add.Count ?? 0) + (plan.DotNetUpdates?.Add.Count ?? 0) is > 0 and var updateCount ? (" " + Ui.PlanUpdates(updateCount)) : string.Empty)
                + (plan.Cleanup is { } cleanupPlan ? (" " + Ui.PlanCleanup((cleanupPlan.ResetBase ? (" " + Ui.PlanResetBase) : string.Empty))) : string.Empty)
                + (plan.WinRe is { } winRePlan ? (" " + Ui.PlanWinRe(winRePlan.Add.Count)) : string.Empty)
                + (plan.Boot is { } bootPlan ? (" " + Ui.PlanBoot(bootPlan.Add.Count)) : string.Empty);
            Status = Ui.PlanReady(plan.Preview.Count, apps, capabilities, features, drivers, updates, encoding);
            Append(Ui.LogPlan(plan.PlanId, plan.WorkspaceRevision, plan.Fingerprint));
        }
        catch (RemoteInvocationException ex)
        {
            Status = (Ui.EngineRejected + " ") + ex.Message;
            Append(ex.ToString());
        }
        catch (ConnectionLostException ex)
        {
            Disconnected(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanPrepare() => !IsBusy && _engine is not null;

    /// <summary>Reads the chosen file here, unelevated; the engine decodes and checks it. No file means the M1 marker.</summary>
    private bool TryReadRegFile(out PlanOptions options)
    {
        options = new PlanOptions(null);
        if (!HasRegFile)
        {
            return true;
        }

        var encoding = (RegFileEncoding)RegFileEncodingIndex;
        if (!int.TryParse(AnsiCodePage, NumberStyles.None, CultureInfo.InvariantCulture, out var codePage) || codePage <= 0)
        {
            Status = Ui.AnsiCodePageInvalid;
            return false;
        }

        try
        {
            var info = new FileInfo(RegFilePath.Trim().Trim('"'));
            if (!info.Exists)
            {
                Status = (Ui.RegFileNotFound + " ") + info.FullName;
                return false;
            }

            if (info.Length > RegFileImport.MaxBytes)
            {
                Status = Ui.RegFileTooLarge(RegFileImport.MaxBytes / 1024);
                return false;
            }

            options = new PlanOptions(new RegFileInput(info.Name, File.ReadAllBytes(info.FullName), encoding, codePage));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Status = (Ui.RegFileReadFailed + " ") + ex.Message;
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken ct)
    {
        var plan = _plan!;
        IsBusy = true;
        PublishedPath = string.Empty;
        Status = Ui.Running;
        Append(Ui.LogRun(plan.PlanId));
        try
        {
            var outcome = await _engine!.Service.StartAsync(
                new StartPlanRequest(plan.PlanId, plan.WorkspaceRevision, Guid.NewGuid()), new UiProgress(this), ct).ConfigureAwait(true);
            DropPlan();
            if (outcome.Rejection != StartPlanRejection.None)
            {
                Status = Ui.RunRejected(outcome.Rejection);
            }
            else if (outcome.Status == RunStatus.Published)
            {
                PublishedPath = outcome.PublishedPath ?? string.Empty;
                Status = Ui.RunDone;
            }
            else
            {
                Status = Ui.RunFailed(outcome.Status, outcome.FailedStep, outcome.Error);
            }

            Append(Ui.LogOutcome(outcome.Rejection, outcome.Status, outcome.FailedStep, outcome.Error, outcome.PublishedPath));
            ShowWorkspace(await _engine.Service.GetWorkspaceAsync(ct).ConfigureAwait(true));
        }
        catch (RemoteInvocationException ex)
        {
            Status = (Ui.EngineError + " ") + ex.Message;
            Append(ex.ToString());
        }
        catch (ConnectionLostException ex)
        {
            Disconnected(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStart() => !IsBusy && HasPlan && _engine is not null;

    [RelayCommand(CanExecute = nameof(CanRecover))]
    private async Task RecoverAsync(CancellationToken ct)
    {
        IsBusy = true;
        Status = Ui.Recovering;
        try
        {
            var result = await _engine!.Service.RecoverAsync(new UiProgress(this), ct).ConfigureAwait(true);
            foreach (var line in result.Actions.Concat(result.Problems))
            {
                Append((Ui.LogRecovery + " ") + line);
            }

            Status = result.Outcome == RecoveryOutcome.StillRequired
                ? Ui.RecoveryFailed
                : Ui.RecoveryDone;
            ShowWorkspace(await _engine.Service.GetWorkspaceAsync(ct).ConfigureAwait(true));
        }
        catch (RemoteInvocationException ex)
        {
            Status = (Ui.EngineError + " ") + ex.Message;
            Append(ex.ToString());
        }
        catch (ConnectionLostException ex)
        {
            Disconnected(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRecover() => !IsBusy && RecoveryRequired && _engine is not null;

    [RelayCommand]
    private void OpenResult()
    {
        if (Directory.Exists(PublishedPath))
        {
            var explorer = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
            explorer.ArgumentList.Add(PublishedPath);
            using var _ = Process.Start(explorer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync().ConfigureAwait(false);
            _engine = null;
        }
    }

    private void ShowEditions(WorkspaceSummary workspace)
    {
        _showingEditions = true;
        try
        {
            _editions = workspace.Editions;
            EditionNames.Clear();
            foreach (var edition in _editions)
            {
                EditionNames.Add($"{edition.Index}. {edition.Name}");
            }

            EditionIndex = _editions.ToList().FindIndex(e => e.Index == workspace.ImageIndex);
        }
        finally
        {
            _showingEditions = false;
        }

        var chosen = _editions.FirstOrDefault(e => e.Index == workspace.ImageIndex);
        EditionStatus = chosen is null
            ? Ui.EditionsNotRead(workspace.ImageIndex)
            : Ui.EditionChosen(chosen.Name, chosen.Index, _editions.Count);
        Append(EditionStatus);
    }

    private void ShowWorkspace(WorkspaceSummary workspace)
    {
        RecoveryRequired = workspace.RecoveryRequired;
        var last = workspace.LastRun is { } run ? Ui.LastRun(run.RunId, run.Status, (run.FailedStep is null ? string.Empty : (" " + Ui.LastRunStep(run.FailedStep)))) : Ui.NoRuns;
        Append(Ui.LogWorkspace(workspace.WorkspaceId, workspace.Revision, workspace.Target, last));
    }

    private static string DescribeRemoval(AppxPackageView package) =>
        Ui.PreviewAppx(package.DisplayName, package.PackageName) + (package.Warning is { } warning ? Environment.NewLine + "  " + Ui.Warning + " " + warning : string.Empty);

    private string DescribeCapabilityRemoval(string name)
    {
        var shown = Capabilities.FirstOrDefault(c => c.Capability.Name == name)?.Title;
        return shown is null || shown == name ? Ui.PreviewCapabilityRemove(name) : Ui.PreviewCapabilityRemoveNamed(shown, name);
    }

    private IEnumerable<string> DescribeCapabilityAdditions(PlanSummary plan)
    {
        foreach (var name in plan.CapabilityAdditions)
        {
            var shown = _available.FirstOrDefault(c => c.Capability.Name == name)?.Title;
            yield return shown is null || shown == name ? Ui.PreviewCapabilityAdd(name) : Ui.PreviewCapabilityAddNamed(shown, name);
        }

        if (plan.CapabilityAdditions.Count > 0)
        {
            yield return ("  " + Ui.PreviewSource(plan.FodSource));
            yield return ("  " + Ui.PreviewCapabilityDependencies);
        }
    }

    private void ShowAvailable()
    {
        var filter = AvailableFilter.Trim();
        AvailableCapabilities.Clear();
        foreach (var item in _available.Where(c => filter.Length == 0
            || c.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
            || c.Details.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            AvailableCapabilities.Add(item);
        }

        ShowAvailableStatus();
    }

    private void ShowAvailableStatus() =>
        AvailableStatus = _available.Count == 0 ? string.Empty
            : Ui.AvailableCount(_available.Count, AvailableCapabilities.Count, _available.Count(c => c.IsSelected == true));

    private void AvailableSelectionChanged()
    {
        DropPlan();
        ShowAvailableStatus();
    }

    private IEnumerable<string> DescribeFeatureChanges(FeatureChangeView? changes)
    {
        if (changes is null)
        {
            yield break;
        }

        foreach (var name in changes.Disable)
        {
            yield return Ui.PreviewFeatureDisable(FeatureTitle(name), (changes.Added.Contains(name) ? (" " + Ui.PreviewFeatureAddedOff) : string.Empty));
        }

        foreach (var name in changes.Enable)
        {
            yield return Ui.PreviewFeatureEnable(FeatureTitle(name), (changes.Added.Contains(name) ? (" " + Ui.PreviewFeatureAddedOn) : string.Empty));
        }

        if (changes.RemovePayload && changes.Disable.Count > 0)
        {
            yield return "  " + Ui.PreviewFeatureRemovePayload;
        }

        if (changes.UsesMediaSxs)
        {
            yield return ("  " + Ui.PreviewNetFx3Source);
        }

        if (changes.Enable.Count > 0)
        {
            yield return ("  " + Ui.PreviewFeatureDefaults);
        }
    }

    private static IEnumerable<string> DescribeDrivers(DriverPlan? drivers)
    {
        if (drivers is null)
        {
            yield break;
        }

        foreach (var removal in drivers.Remove)
        {
            yield return Ui.PreviewDriverRemove(removal.Description, (removal.BootCritical ? (" " + Ui.PreviewDriverBootCritical) : string.Empty));
        }

        foreach (var addition in drivers.Add)
        {
            yield return Ui.PreviewDriverAdd(addition.Description, (addition.ForceUnsigned ? (" " + Ui.PreviewDriverUnsigned) : string.Empty));
            if (addition.SameContent.Count > 0)
            {
                yield return ("  " + Ui.PreviewDriverSameContent(string.Join(", ", addition.SameContent)));
            }
        }

        if (drivers.Source is { } source)
        {
            yield return ("  " + Ui.PreviewSource(source));
        }

        foreach (var warning in drivers.Warnings)
        {
            yield return "  " + Ui.Warning + " " + warning;
        }
    }

    private static IEnumerable<string> DescribeUpdates(UpdatePlan? updates)
    {
        if (updates is null)
        {
            yield break;
        }

        foreach (var addition in updates.Add)
        {
            yield return Ui.PreviewUpdateAdd(addition.Description);
            if (addition.SameContent.Count > 0)
            {
                yield return ("  " + Ui.PreviewUpdateSameContent(string.Join(", ", addition.SameContent)));
            }
        }

        if (updates.Add.Count > 0)
        {
            yield return ("  " + Ui.PreviewUpdateDuration);
        }

        if (updates.Source is { } source)
        {
            yield return ("  " + Ui.PreviewSource(source));
        }

        foreach (var warning in updates.Warnings)
        {
            yield return "  " + Ui.Warning + " " + warning;
        }
    }

    private static IEnumerable<string> DescribeDotNet(UpdatePlan? dotNet)
    {
        if (dotNet is null)
        {
            yield break;
        }

        yield return Ui.PreviewDotNet;
        foreach (var addition in dotNet.Add)
        {
            yield return "  " + Ui.PreviewUpdateAdd(addition.Description);
        }
    }

    private static IEnumerable<string> DescribeWinRe(WinRePlan? winRe)
    {
        if (winRe is null)
        {
            yield break;
        }

        yield return Ui.PreviewWinRe;
        foreach (var addition in winRe.Add)
        {
            yield return ("  " + Ui.PreviewWinReAdd(addition.Description));
        }

        foreach (var warning in winRe.Warnings)
        {
            yield return "  " + Ui.Warning + " " + warning;
        }
    }

    private static IEnumerable<string> DescribeBoot(BootPlan? boot)
    {
        if (boot is null)
        {
            yield break;
        }

        yield return Ui.PreviewBoot;
        foreach (var addition in boot.Add)
        {
            yield return ("  " + Ui.PreviewBootAdd(addition.Description));
        }

        foreach (var warning in boot.Warnings)
        {
            yield return "  " + Ui.Warning + " " + warning;
        }
    }

    private static IEnumerable<string> DescribeCleanup(CleanupPlan? cleanup)
    {
        if (cleanup is null)
        {
            yield break;
        }

        yield return cleanup.ResetBase
            ? Ui.PreviewCleanupResetBase
            : Ui.PreviewCleanup;
        foreach (var warning in cleanup.Warnings)
        {
            yield return "  " + Ui.Warning + " " + warning;
        }
    }

    private void ShowUpdates(InspectionSummary inspection)
    {
        UpdatesStatus = inspection.PackageProblems.Count > 0
            ? (Ui.PackagesNotRead + " ") + string.Join("; ", inspection.PackageProblems)
            : Ui.PackagesStatus(inspection.PackageCount, inspection.Rollup ?? Ui.None, inspection.ServicingStack ?? Ui.None);
    }

    private void ShowUpdatePackagesStatus() =>
        UpdatePackagesStatus = (Ui.UpdateFilesFound(UpdatePackages.Count, UpdatePackages.Count(p => p.CanSelect)) + " ") +
            (Ui.UpdateFilesTargets(UpdatePackages.Count(p => p.IsSelected == true), UpdatePackages.Count(p => p.IsForWinRe == true)) + " ") +
            Ui.UpdateFilesBoot(UpdatePackages.Count(p => p.IsForBoot == true));

    private void UpdateSelectionChanged()
    {
        DropPlan();
        ShowUpdatePackagesStatus();
    }

    private void ShowDrivers(InspectionSummary inspection)
    {
        if (inspection.DriverProblems.Count > 0)
        {
            DriversStatus = (Ui.ImageDriversNotRead + " ") + string.Join("; ", inspection.DriverProblems);
            return;
        }

        foreach (var driver in inspection.Drivers)
        {
            ImageDrivers.Add(new InstalledDriverItem(driver, DropPlan));
        }

        DriversStatus = inspection.Drivers.Count == 0
            ? Ui.ImageDriversNone
            : Ui.ImageDriversCount(inspection.Drivers.Count, inspection.Drivers.Count(d => d.BootCritical));
    }

    private void ShowDriverPackagesStatus() =>
        DriverPackagesStatus = Ui.DriverPackagesCount(DriverPackages.Count, DriverPackages.Count(p => p.CanSelect), DriverPackages.Count(p => p.IsSelected == true));

    private void DriverSelectionChanged()
    {
        DropPlan();
        ShowDriverPackagesStatus();
    }

    private string FeatureTitle(string name)
    {
        var shown = Features.FirstOrDefault(f => f.Feature.Name == name)?.Title;
        return shown is null || shown == name ? name : $"{shown} ({name})";
    }

    private void ShowFeatures(InspectionSummary inspection)
    {
        if (inspection.FeatureProblems.Count > 0)
        {
            FeaturesStatus = (Ui.FeaturesBlocked + " ") + string.Join("; ", inspection.FeatureProblems);
            return;
        }

        foreach (var feature in inspection.Features)
        {
            Features.Add(new FeatureItem(feature, DropPlan));
        }

        FeaturesStatus = (Ui.FeaturesCount(inspection.Features.Count, inspection.Features.Count(f => f.IsOn)) + " ")
            + Ui.FeaturesHint;
    }

    private void ShowCapabilities(InspectionSummary inspection)
    {
        if (inspection.CapabilityProblems.Count > 0)
        {
            CapabilitiesStatus = (Ui.CapabilitiesNotReadReason + " ") + string.Join("; ", inspection.CapabilityProblems);
            return;
        }

        foreach (var capability in inspection.Capabilities)
        {
            Capabilities.Add(new CapabilityItem(capability, DropPlan));
        }

        _available.AddRange(inspection.AvailableCapabilities.Select(c => new CapabilityItem(c, AvailableSelectionChanged)));
        ShowAvailable();
        CapabilitiesStatus = Ui.CapabilitiesCount(inspection.Capabilities.Count);
    }

    private void ClearInventory()
    {
        Apps.Clear();
        Features.Clear();
        Capabilities.Clear();
        _available.Clear();
        ShowAvailable();
        ImageDrivers.Clear();

        // Package choices and the boot-critical and ResetBase approvals were made for the image that is gone.
        foreach (var package in DriverPackages)
        {
            package.IsSelected = false;
        }

        AllowBootCritical = false;
        RemoveFeaturePayload = false;
        ResetBase = false;

        // The preview described the plan for the image that is gone.
        OperationPreview = string.Empty;
        foreach (var package in UpdatePackages)
        {
            package.IsSelected = false;
            package.IsForWinRe = false;
            package.IsForBoot = false;
        }

        UpdatesStatus = Ui.UpdatesNotRead;
    }

    private void FoldersChanged(bool keepEditions = false)
    {
        DropPlan();
        _opened = null;
        if (!keepEditions && _editions.Count > 0)
        {
            _showingEditions = true;
            _editions = [];
            EditionNames.Clear();
            EditionIndex = -1;
            _showingEditions = false;
            EditionStatus = Ui.FoldersChangedEditions;
        }

        if (Apps.Count > 0 || Features.Count > 0 || Capabilities.Count > 0 || ImageDrivers.Count > 0)
        {
            ClearInventory();
            AppsStatus = Ui.FoldersChangedApps;
            FeaturesStatus = Ui.FeaturesNotRead;
            CapabilitiesStatus = Ui.CapabilitiesNotRead;
            DriversStatus = Ui.DriversNotRead;
        }
    }

    private void DropPlan()
    {
        _plan = null;
        HasPlan = false;
    }

    private void Disconnected(Exception ex)
    {
        _engine = null;
        _opened = null;
        DropPlan();
        ClearInventory();
        OnPropertyChanged(nameof(IsConnected));
        ConnectEngineCommand.NotifyCanExecuteChanged();
        PrepareCommand.NotifyCanExecuteChanged();
        InspectCommand.NotifyCanExecuteChanged();
        Status = Ui.EngineLost;
        Append(ex.ToString());
    }

    private static string Describe(LocationProblem problem) => problem.Kind switch
    {
        LocationProblemKind.NotLocalPath => Ui.LocationNotLocal(Name(problem.Location)),
        LocationProblemKind.Same => Ui.LocationSame(Name(problem.Location), Name(problem.Other)),
        _ => Ui.LocationNested(Name(problem.Location), Name(problem.Other)),
    };

    private static string Name(string? location) => location switch
    {
        WorkspaceLocations.Source => Ui.LocationSource,
        WorkspaceLocations.WorkDir => Ui.LocationWorkDir,
        WorkspaceLocations.OutputDir => Ui.LocationOutputDir,
        _ => location ?? string.Empty,
    };

    private void Append(string line)
    {
        var text = $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
        if (_dispatcher.HasThreadAccess)
        {
            Log += text;
        }
        else
        {
            _dispatcher.TryEnqueue(() => Log += text);
        }
    }

    private sealed class UiProgress(MainViewModel owner) : IProgress<string>
    {
        public void Report(string value) => owner.Append(value);
    }
}
