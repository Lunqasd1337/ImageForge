using ImageForge.Core.Changes;

namespace ImageForge.Core.Registry.RegFile;

/// <summary>A .reg file as the window sends it to the engine: the raw bytes and how the user wants them decoded.</summary>
public sealed record RegFileInput(string FileName, byte[] Content, RegFileEncoding Encoding, int AnsiCodePage);

/// <param name="Changes">Null when any diagnostic blocks the file.</param>
/// <param name="Encoding">How the bytes were decoded; null when decoding was blocked.</param>
public sealed record RegFileImportResult(
    IReadOnlyList<ImageChange>? Changes,
    IReadOnlyList<RegFileDiagnostic> Diagnostics,
    RegFileEncodingKind? Encoding,
    int? CodePage);

public static class RegFileImport
{
    /// <summary>Keeps the request under the engine's 1 MiB RPC limit after base64; tweak files are far smaller.</summary>
    public const int MaxBytes = 512 * 1024;

    public static RegFileImportResult Read(RegFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Content is null || input.Content.Length > MaxBytes)
        {
            return Blocked(new RegFileDiagnostic(RegFileCodes.TooLarge, 0, $"The file is larger than {MaxBytes / 1024} KiB."), null, null);
        }

        var text = new RegFileLoader().Decode(input.Content, input.Encoding, input.AnsiCodePage);
        if (text.Text is null || text.Diagnostics.Count > 0)
        {
            return new RegFileImportResult(null, text.Diagnostics, text.Encoding, text.CodePage);
        }

        var parsed = new RegFileParser().Parse(text.Text);
        if (!parsed.IsValid)
        {
            return new RegFileImportResult(null, parsed.Diagnostics, text.Encoding, text.CodePage);
        }

        return parsed.Operations.Count == 0
            ? Blocked(new RegFileDiagnostic(RegFileCodes.Empty, 0, "The file has no keys or values."), text.Encoding, text.CodePage)
            : new RegFileImportResult(RegFileChanges.From(parsed), [], text.Encoding, text.CodePage);
    }

    private static RegFileImportResult Blocked(RegFileDiagnostic diagnostic, RegFileEncodingKind? encoding, int? codePage) =>
        new(null, [diagnostic], encoding, codePage);
}

/// <summary>The Russian text of a .reg diagnostic for the window; the parser's own message follows as detail.</summary>
public static class RegFileDiagnosticText
{
    public static string Describe(RegFileDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var text = diagnostic.Code switch
        {
            RegFileCodes.EncodingAmbiguous => "Кодировку нельзя определить: в файле нет BOM. Выберите UTF-16, UTF-8 или ANSI с кодовой страницей",
            RegFileCodes.EncodingInvalid => "Байты файла не подходят к выбранной кодировке",
            RegFileCodes.EncodingUnsupported => "Эта кодировка или кодовая страница не поддерживается",
            RegFileCodes.HeaderMissing => "Первая строка должна быть «Windows Registry Editor Version 5.00»",
            RegFileCodes.FormatUnsupported => "Формат REGEDIT4 не поддерживается, нужен «Windows Registry Editor Version 5.00»",
            RegFileCodes.KeyMalformed => "Неверная строка ключа",
            RegFileCodes.ValueOutsideKey => "Значение стоит до первого [ключа]",
            RegFileCodes.ValueMalformed => "Неверная запись значения",
            RegFileCodes.ValueTypeUnsupported => "Тип значения не поддерживается",
            RegFileCodes.LineUnrecognized => "Строка не похожа на ключ, значение или комментарий",
            RegFileCodes.TooLarge => $"Файл больше {RegFileImport.MaxBytes / 1024} КБ",
            RegFileCodes.Empty => "В файле нет ни одного ключа или значения",
            _ => "Ошибка в файле",
        };

        return diagnostic.Line > 0 ? $"строка {diagnostic.Line}: {text} ({diagnostic.Message})" : $"{text} ({diagnostic.Message})";
    }
}
