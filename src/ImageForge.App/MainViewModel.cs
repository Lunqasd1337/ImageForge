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

    /// <summary>Provisioned packages of the last inspection; empty until «Прочитать приложения».</summary>
    public ObservableCollection<AppxItem> Apps { get; } = [];

    [ObservableProperty]
    public partial string AppsStatus { get; set; } = "Список ещё не прочитан. Чтение копирует дистрибутив в рабочую папку (если копии ещё нет) и монтирует её только для чтения.";

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
        Apps.Clear();
        IsBusy = true;
        try
        {
            if (!await EnsureWorkspaceAsync(ct).ConfigureAwait(true))
            {
                return;
            }

            Status = "Чтение списка приложений из образа...";
            var inspection = await _engine!.Service.InspectAsync(new UiProgress(this), ct).ConfigureAwait(true);
            if (inspection.Problems.Count > 0)
            {
                AppsStatus = "Список не прочитан: " + string.Join("; ", inspection.Problems);
                Status = AppsStatus;
                ShowWorkspace(await _engine.Service.GetWorkspaceAsync(ct).ConfigureAwait(true));
                return;
            }

            foreach (var package in inspection.Packages)
            {
                Apps.Add(new AppxItem(package, DropPlan));
            }

            AppsStatus = $"Предустановленных приложений: {inspection.Packages.Count}. Отметьте те, что нужно удалить, и нажмите «Подготовить».";
            Status = AppsStatus;
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

        options = options with { RemoveAppx = [.. Apps.Where(a => a.IsSelected == true).Select(a => a.Package.FamilyName).Distinct()] };
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
            OperationPreview = string.Join(Environment.NewLine, plan.AppxRemovals.Select(DescribeRemoval).Concat(plan.Preview.Select(RegistryPreviewText.Describe)));
            var apps = plan.AppxRemovals.Count > 0 ? $" Удаляемых приложений: {plan.AppxRemovals.Count}." : string.Empty;
            Status = $"План готов, изменений реестра: {plan.Preview.Count}.{apps}{encoding} Проверьте превью и нажмите «Запустить».";
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

    private void FoldersChanged()
    {
        DropPlan();
        _opened = null;
        if (Apps.Count > 0)
        {
            Apps.Clear();
            AppsStatus = "Папки изменились; прочитайте список приложений снова.";
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
        Apps.Clear();
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
