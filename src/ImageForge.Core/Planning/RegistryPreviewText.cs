using ImageForge.Core.Registry;

namespace ImageForge.Core.Planning;

/// <summary>The Russian text of one registry change in the preview, shared by the window and the experiments tool.</summary>
public static class RegistryPreviewText
{
    public static string Describe(RegistryWritePreview change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var action = change.Kind switch
        {
            RegistryChangeKind.SetValue => "Записать значение",
            RegistryChangeKind.DeleteValue => "Удалить значение",
            RegistryChangeKind.CreateKey => "Создать ключ",
            RegistryChangeKind.DeleteKey => "Удалить ключ со всем содержимым",
            _ => change.Kind.ToString(),
        };
        var target = change.UnderCurrentControlSet
            ? $@"{change.SubKey} в ControlSet, который указан в SYSTEM\Select\Current"
            : change.ClassesRootPolicy
                ? $@"SOFTWARE\{change.SubKey} (HKCR по политике ImageForge записывается в машинные классы)"
                : change.SubKey.Length == 0 ? "(корень куста)" : change.SubKey;
        var lines = new List<string>
        {
            $"{action}: {change.SourcePath}",
            change.FirstLogon
                ? $@"  При первом входе каждого пользователя (Active Setup, reg import из {FirstLogonDelivery.Directory}), ключ HKCU\Software\Classes{(change.SubKey.Length == 0 ? string.Empty : "\\" + change.SubKey)}"
                : $"  Куст: офлайн {change.Hive} ({change.HiveFile}), ключ {target}",
        };
        if (change.ValueName is { } name)
        {
            var shown = name.Length == 0 ? "(по умолчанию)" : name;
            lines.Add(change.NewValue is { } value ? $"  Значение: {shown} = {value.Describe()}" : $"  Значение: {shown}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
