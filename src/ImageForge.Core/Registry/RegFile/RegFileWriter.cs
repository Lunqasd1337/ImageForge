using System.Globalization;
using System.Text;
using ImageForge.Core.Changes;
using ImageForge.Core.Planning;

namespace ImageForge.Core.Registry.RegFile;

/// <summary>Writes changes as a Windows Registry Editor Version 5.00 file: UTF-16LE with BOM, CRLF.</summary>
public static class RegFileWriter
{
    private const int HexBytesPerLine = 25;

    /// <summary>True when a .reg file can hold the name exactly (a line break cannot be written in one).</summary>
    public static bool CanHold(string name) => name.IndexOfAny(['\r', '\n']) < 0;

    /// <param name="root">The full key the changes' SubKey is relative to, such as HKEY_CURRENT_USER\Software\Classes.</param>
    public static byte[] Write(string root, IReadOnlyList<RegistryWritePreview> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Text(root, changes))];
    }

    private static string Text(string root, IReadOnlyList<RegistryWritePreview> changes)
    {
        var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        string? openKey = null;
        foreach (var change in changes)
        {
            var key = change.SubKey.Length == 0 ? root : $@"{root}\{change.SubKey}";
            if (change.Kind == RegistryChangeKind.DeleteKey)
            {
                text.Append(CultureInfo.InvariantCulture, $"\r\n[-{key}]\r\n");
                openKey = null;
                continue;
            }

            if (change.Kind == RegistryChangeKind.CreateKey || !string.Equals(openKey, key, StringComparison.Ordinal))
            {
                text.Append(CultureInfo.InvariantCulture, $"\r\n[{key}]\r\n");
                openKey = key;
            }

            if (change.ValueName is not { } name)
            {
                continue;
            }

            text.Append(name.Length == 0 ? "@" : Quote(name)).Append('=');
            text.Append(change.Kind == RegistryChangeKind.DeleteValue ? "-" : Data(change.NewValue!)).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>The form regedit exports: quoted REG_SZ and dword: when they are exact, hex otherwise.</summary>
    private static string Data(RegistryValue value)
    {
        if (value.Type == RegistryValueType.Sz && value.Text is { } text && CanHold(text) && RegistryValue.Sz(text).Equals(value))
        {
            return Quote(text);
        }

        if (value.Type == RegistryValueType.Dword && value.Bytes.Length == 4)
        {
            return string.Create(CultureInfo.InvariantCulture, $"dword:{BitConverter.ToUInt32(value.Bytes):x8}");
        }

        var prefix = value.Type == RegistryValueType.Binary ? "hex:" : string.Create(CultureInfo.InvariantCulture, $"hex({(uint)value.Type:x}):");
        var hex = new StringBuilder(prefix);
        for (var i = 0; i < value.Bytes.Length; i++)
        {
            if (i > 0)
            {
                hex.Append(i % HexBytesPerLine == 0 ? ",\\\r\n  " : ",");
            }

            hex.Append(value.Bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
