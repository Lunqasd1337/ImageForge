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
            RegistryChangeKind.SetValue => Messages.RegPreviewSetValue,
            RegistryChangeKind.DeleteValue => Messages.RegPreviewDeleteValue,
            RegistryChangeKind.CreateKey => Messages.RegPreviewCreateKey,
            RegistryChangeKind.DeleteKey => Messages.RegPreviewDeleteKey,
            _ => change.Kind.ToString(),
        };
        var target = change.UnderCurrentControlSet
            ? Messages.RegPreviewCurrentControlSet(change.SubKey)
            : change.ClassesRootPolicy
                ? Messages.RegPreviewClassesRoot(change.SubKey)
                : change.SubKey.Length == 0 ? Messages.RegPreviewHiveRoot : change.SubKey;
        var lines = new List<string>
        {
            $"{action}: {change.SourcePath}",
            change.FirstLogon
                ? Messages.RegPreviewFirstLogon(FirstLogonDelivery.Directory, (change.SubKey.Length == 0 ? string.Empty : "\\" + change.SubKey))
                : Messages.RegPreviewHive(change.Hive, change.HiveFile, target),
        };
        if (change.ValueName is { } name)
        {
            var shown = name.Length == 0 ? Messages.RegPreviewDefaultValue : name;
            lines.Add(change.NewValue is { } value ? Messages.RegPreviewValueData(shown, value.Describe()) : Messages.RegPreviewValue(shown));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
