using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageForge.Core.Contracts;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry.RegFile;
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

    [ObservableProperty]
    public partial string SourcePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WorkPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OutputPath { get; set; } = string.Empty;

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
    public partial string Status { get; set; } = "Движок не подключён.";

    [ObservableProperty]
    public partial string Log { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OperationPreview { get; set; } = "Превью появится после подготовки: укажите папки и нажмите «Подготовить».";

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
    public partial string AppsStatus { get; set; } = "Образ ещё не прочитан. Чтение копирует дистрибутив в рабочую папку (если копии ещё нет) и монтирует её только для чтения.";

    /// <summary>Windows features of the last inspection; the check box is the wanted state.</summary>
    public ObservableCollection<FeatureItem> Features { get; } = [];

    [ObservableProperty]
    public partial string FeaturesStatus { get; set; } = "Компоненты ещё не прочитаны.";

    /// <summary>Installed capabilities of the last inspection; a check marks one for removal.</summary>
    public ObservableCollection<CapabilityItem> Capabilities { get; } = [];

    [ObservableProperty]
    public partial string CapabilitiesStatus { get; set; } = "Дополнительные компоненты ещё не прочитаны.";

    public bool IsConnected => _engine is not null;

    public bool HasResult => PublishedPath.Length > 0;

    public bool HasRegFile => RegFilePath.Length > 0;

    public bool IsAnsi => RegFileEncodingIndex == (int)RegFileEncoding.Ansi;

    partial void OnPublishedPathChanged(string value) => OnPropertyChanged(nameof(HasResult));

    // Any change of the folders invalidates the shown plan and the inventory; they must be read again.
    partial void OnSourcePathChanged(string value) => FoldersChanged();

    partial void OnWorkPathChanged(string value) => FoldersChanged();

    partial void OnOutputPathChanged(string value) => FoldersChanged();

    partial void OnRegFilePathChanged(string value) => DropPlan();

    partial void OnRegFileEncodingIndexChanged(int value) => DropPlan();

    partial void OnAnsiCodePageChanged(string value) => DropPlan();

    [RelayCommand]
    private void ClearRegFile() => RegFilePath = string.Empty;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectEngineAsync(CancellationToken ct)
    {
        Status = "Запуск движка (потребуется подтверждение UAC)...";
        IsBusy = true;
        try
        {
            _engine = await EngineClient.StartAsync(ct).ConfigureAwait(true);
            var info = _engine.Info;
            Status = "Движок подключён. Укажите папки и нажмите «Подготовить».";
            Append($"Движок {info.EngineVersion}, протокол {info.ProtocolVersion}, PID {info.ProcessId}, экземпляр {info.InstanceId}");
        }
        catch (EngineLaunchException ex)
        {
            Status = ex.Message;
            Append(ex.ToString());
        }
        catch (Win32Exception ex)
        {
            Status = "Не удалось запустить движок.";
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

            Status = "Чтение приложений и компонентов из образа...";
            var inspection = await _engine!.Service.InspectAsync(new UiProgress(this), ct).ConfigureAwait(true);
            if (inspection.Problems.Count > 0)
            {
                AppsStatus = "Образ не прочитан: " + string.Join("; ", inspection.Problems);
                Status = AppsStatus;
                ShowWorkspace(await _engine.Service.GetWorkspaceAsync(ct).ConfigureAwait(true));
                return;
            }

            foreach (var package in inspection.Packages)
            {
                Apps.Add(new AppxItem(package, DropPlan));
            }

            AppsStatus = $"Предустановленных приложений: {inspection.Packages.Count}. Отметьте те, что нужно удалить.";
            ShowFeatures(inspection);
            ShowCapabilities(inspection);
            Status = "Образ прочитан. Отметьте изменения и нажмите «Подготовить».";
        }
        catch (RemoteInvocationException ex)
        {
            Status = "Движок отклонил запрос: " + ex.Message;
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
        var request = new OpenWorkspaceRequest(SourcePath.Trim(), WorkPath.Trim(), OutputPath.Trim());
        var workspace = request == _opened
            ? await _engine!.Service.GetWorkspaceAsync(ct).ConfigureAwait(true)
            : await _engine!.Service.OpenWorkspaceAsync(request, ct).ConfigureAwait(true);
        if (workspace.Problems.Count > 0)
        {
            _opened = null;
            Status = "Папки не подходят: " + string.Join("; ", workspace.Problems.Select(Describe));
            return false;
        }

        _opened = request;
        ShowWorkspace(workspace);
        if (workspace.RecoveryRequired)
        {
            Status = "Предыдущий запуск или чтение не завершены. Нажмите «Восстановить» и повторите.";
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
        };
        IsBusy = true;
        try
        {
            if (!await EnsureWorkspaceAsync(ct).ConfigureAwait(true))
            {
                return;
            }

            var plan = await _engine!.Service.PlanAsync(options, ct).ConfigureAwait(true);
            var encoding = plan.FileEncoding is null ? string.Empty : $" Кодировка файла: {plan.FileEncoding}.";
            if (plan.FileDiagnostics.Count > 0)
            {
                Status = $"Файл .reg не принят, ничего не будет применено.{encoding}";
                OperationPreview = string.Join(Environment.NewLine, plan.FileDiagnostics.Select(RegFileDiagnosticText.Describe));
                return;
            }

            if (plan.Diagnostics.Count > 0)
            {
                Status = "План не построен, ничего не будет применено.";
                OperationPreview = string.Join(Environment.NewLine, plan.Diagnostics.Select(d => $"изменение {d.ChangeIndex + 1}: {d.Message}"));
                return;
            }

            _plan = plan;
            HasPlan = true;
            string[] lines =
            [
                .. plan.AppxRemovals.Select(DescribeRemoval),
                .. plan.CapabilityRemovals.Select(DescribeCapabilityRemoval),
                .. DescribeFeatureChanges(plan.FeatureChanges),
                .. plan.Preview.Select(RegistryPreviewText.Describe),
            ];
            OperationPreview = string.Join(Environment.NewLine, lines);
            var apps = plan.AppxRemovals.Count > 0 ? $" Удаляемых приложений: {plan.AppxRemovals.Count}." : string.Empty;
            var capabilities = plan.CapabilityRemovals.Count > 0 ? $" Удаляемых дополнительных компонентов: {plan.CapabilityRemovals.Count}." : string.Empty;
            var features = plan.FeatureChanges is { } f ? $" Включаемых компонентов: {f.Enable.Count}, выключаемых: {f.Disable.Count}." : string.Empty;
            Status = $"План готов, изменений реестра: {plan.Preview.Count}.{apps}{capabilities}{features}{encoding} Проверьте превью и нажмите «Запустить».";
            Append($"План {plan.PlanId}, ревизия {plan.WorkspaceRevision}, отпечаток {plan.Fingerprint}");
        }
        catch (RemoteInvocationException ex)
        {
            Status = "Движок отклонил запрос: " + ex.Message;
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
            Status = "Кодовая страница ANSI должна быть числом, например 1251.";
            return false;
        }

        try
        {
            var info = new FileInfo(RegFilePath.Trim().Trim('"'));
            if (!info.Exists)
            {
                Status = "Файл .reg не найден: " + info.FullName;
                return false;
            }

            if (info.Length > RegFileImport.MaxBytes)
            {
                Status = $"Файл .reg больше {RegFileImport.MaxBytes / 1024} КБ, такие файлы не принимаются.";
                return false;
            }

            options = new PlanOptions(new RegFileInput(info.Name, File.ReadAllBytes(info.FullName), encoding, codePage));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Status = "Не удалось прочитать файл .reg: " + ex.Message;
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken ct)
    {
        var plan = _plan!;
        IsBusy = true;
        PublishedPath = string.Empty;
        Status = "Выполняется. Не закрывайте окно: прерванный запуск потребует восстановления.";
        Append($"Запуск плана {plan.PlanId}");
        try
        {
            var outcome = await _engine!.Service.StartAsync(
                new StartPlanRequest(plan.PlanId, plan.WorkspaceRevision, Guid.NewGuid()), new UiProgress(this), ct).ConfigureAwait(true);
            DropPlan();
            if (outcome.Rejection != StartPlanRejection.None)
            {
                Status = $"Запуск отклонён ({outcome.Rejection}). Нажмите «Подготовить» ещё раз.";
            }
            else if (outcome.Status == RunStatus.Published)
            {
                PublishedPath = outcome.PublishedPath ?? string.Empty;
                Status = "Готово. Результат сохранён.";
            }
            else
            {
                Status = $"Запуск не удался ({outcome.Status}) на шаге {outcome.FailedStep}: {outcome.Error}. Исходник и прежний результат не изменены.";
            }

            Append($"Итог: {outcome.Rejection} {outcome.Status} {outcome.FailedStep} {outcome.Error} {outcome.PublishedPath}");
            ShowWorkspace(await _engine.Service.GetWorkspaceAsync(ct).ConfigureAwait(true));
        }
        catch (RemoteInvocationException ex)
        {
            Status = "Ошибка движка: " + ex.Message;
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
        Status = "Восстановление...";
        try
        {
            var result = await _engine!.Service.RecoverAsync(new UiProgress(this), ct).ConfigureAwait(true);
            foreach (var line in result.Actions.Concat(result.Problems))
            {
                Append("Восстановление: " + line);
            }

            Status = result.Outcome == RecoveryOutcome.StillRequired
                ? "Восстановить автоматически не удалось. Пришлите журнал."
                : "Восстановление завершено. Нажмите «Подготовить».";
            ShowWorkspace(await _engine.Service.GetWorkspaceAsync(ct).ConfigureAwait(true));
        }
        catch (RemoteInvocationException ex)
        {
            Status = "Ошибка движка: " + ex.Message;
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

    private void ShowWorkspace(WorkspaceSummary workspace)
    {
        RecoveryRequired = workspace.RecoveryRequired;
        var last = workspace.LastRun is { } run ? $"последний запуск {run.RunId:N}: {run.Status}{(run.FailedStep is null ? string.Empty : $" на шаге {run.FailedStep}")}" : "запусков не было";
        Append($"Рабочая папка {workspace.WorkspaceId}, ревизия {workspace.Revision}, цель {workspace.Target}, {last}");
    }

    private static string DescribeRemoval(AppxPackageView package) =>
        $"Удалить приложение {package.DisplayName} ({package.PackageName})" + (package.Warning is { } warning ? $"{Environment.NewLine}  Внимание: {warning}" : string.Empty);

    private string DescribeCapabilityRemoval(string name)
    {
        var shown = Capabilities.FirstOrDefault(c => c.Capability.Name == name)?.Title;
        return shown is null || shown == name ? $"Удалить дополнительный компонент {name}" : $"Удалить дополнительный компонент {shown} ({name})";
    }

    private IEnumerable<string> DescribeFeatureChanges(FeatureChangeView? changes)
    {
        if (changes is null)
        {
            yield break;
        }

        foreach (var name in changes.Disable)
        {
            yield return $"Выключить компонент {FeatureTitle(name)}{(changes.Added.Contains(name) ? " (добавлен планом: зависит от выключаемого)" : string.Empty)}";
        }

        foreach (var name in changes.Enable)
        {
            yield return $"Включить компонент {FeatureTitle(name)}{(changes.Added.Contains(name) ? " (добавлен планом: нужен включаемому)" : string.Empty)}";
        }

        if (changes.UsesMediaSxs)
        {
            yield return "  Файлы .NET Framework 3.5 будут взяты из sources\\sxs дистрибутива.";
        }

        if (changes.Enable.Count > 0)
        {
            yield return "  DISM может сам включить вложенные компоненты по умолчанию; они будут перечислены в журнале запуска.";
        }
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
            FeaturesStatus = "Компоненты изменить нельзя: " + string.Join("; ", inspection.FeatureProblems);
            return;
        }

        foreach (var feature in inspection.Features)
        {
            Features.Add(new FeatureItem(feature, DropPlan));
        }

        FeaturesStatus = $"Компонентов: {inspection.Features.Count}, включено: {inspection.Features.Count(f => f.IsOn)}. "
            + "Отметка означает «включён после запуска»: снимите её, чтобы выключить, поставьте, чтобы включить.";
    }

    private void ShowCapabilities(InspectionSummary inspection)
    {
        if (inspection.CapabilityProblems.Count > 0)
        {
            CapabilitiesStatus = "Дополнительные компоненты не прочитаны: " + string.Join("; ", inspection.CapabilityProblems);
            return;
        }

        foreach (var capability in inspection.Capabilities)
        {
            Capabilities.Add(new CapabilityItem(capability, DropPlan));
        }

        CapabilitiesStatus = $"Установленных дополнительных компонентов: {inspection.Capabilities.Count}. Отметьте те, что нужно удалить.";
    }

    private void ClearInventory()
    {
        Apps.Clear();
        Features.Clear();
        Capabilities.Clear();
    }

    private void FoldersChanged()
    {
        DropPlan();
        _opened = null;
        if (Apps.Count > 0 || Features.Count > 0 || Capabilities.Count > 0)
        {
            ClearInventory();
            AppsStatus = "Папки изменились; прочитайте образ снова.";
            FeaturesStatus = "Компоненты ещё не прочитаны.";
            CapabilitiesStatus = "Дополнительные компоненты ещё не прочитаны.";
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
        Status = "Связь с движком потеряна. Подключите движок снова; незавершённый запуск потребует восстановления.";
        Append(ex.ToString());
    }

    private static string Describe(LocationProblem problem) => problem.Kind switch
    {
        LocationProblemKind.NotLocalPath => $"{Name(problem.Location)}: нужен полный путь на локальном диске",
        LocationProblemKind.Same => $"{Name(problem.Location)} и {Name(problem.Other)} совпадают",
        _ => $"{Name(problem.Location)} находится внутри папки «{Name(problem.Other)}»",
    };

    private static string Name(string? location) => location switch
    {
        WorkspaceLocations.Source => "источник",
        WorkspaceLocations.WorkDir => "рабочая папка",
        WorkspaceLocations.OutputDir => "папка результата",
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
