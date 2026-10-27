using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageForge.Core.Changes;

namespace ImageForge.App;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private EngineClient? _engine;

    [ObservableProperty]
    public partial string SourcePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WorkPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = "Движок не подключён.";

    [ObservableProperty]
    public partial string Log { get; set; } = string.Empty;

    public string OperationPreview { get; } =
        $"""
        Куст: офлайн SOFTWARE (Windows\System32\config\SOFTWARE)
        Ключ: {ValidationMarker.M1.KeyPath}
        Значение: {ValidationMarker.M1.ValueName} (REG_SZ) = {ValidationMarker.M1.Value.Data}
        """;

    [RelayCommand]
    private async Task ConnectEngineAsync(CancellationToken ct)
    {
        if (_engine is not null)
        {
            return;
        }

        Status = "Запуск движка (потребуется подтверждение UAC)...";
        try
        {
            _engine = await EngineClient.StartAsync(ct).ConfigureAwait(true);
            var info = _engine.Info;
            Status = "Движок подключён.";
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
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync().ConfigureAwait(false);
            _engine = null;
        }
    }

    private void Append(string line) => Log += $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
}
