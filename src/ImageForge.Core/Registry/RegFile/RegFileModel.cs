using ImageForge.Core.Changes;

namespace ImageForge.Core.Registry.RegFile;

/// <param name="Line">1-based line in the file; 0 when the problem is the file as a whole.</param>
public sealed record RegFileDiagnostic(string Code, int Line, string Message);

public static class RegFileCodes
{
    public const string EncodingAmbiguous = "regfile.encoding.ambiguous";
    public const string EncodingInvalid = "regfile.encoding.invalid";
    public const string EncodingUnsupported = "regfile.encoding.unsupported";
    public const string HeaderMissing = "regfile.header.missing";
    public const string FormatUnsupported = "regfile.format.unsupported";
    public const string KeyMalformed = "regfile.key.malformed";
    public const string ValueOutsideKey = "regfile.value.outside-key";
    public const string ValueMalformed = "regfile.value.malformed";
    public const string ValueTypeUnsupported = "regfile.value.type-unsupported";
    public const string LineUnrecognized = "regfile.line.unrecognized";
}

/// <param name="Line">1-based line where the operation starts.</param>
public abstract record RegFileOperation(int Line, string KeyPath);

public sealed record CreateRegKey(int Line, string KeyPath) : RegFileOperation(Line, KeyPath);

public sealed record DeleteRegKey(int Line, string KeyPath) : RegFileOperation(Line, KeyPath);

/// <param name="Name">Empty string addresses the default value.</param>
public sealed record SetRegValue(int Line, string KeyPath, string Name, RegistryValue Data) : RegFileOperation(Line, KeyPath);

/// <param name="Name">Empty string addresses the default value.</param>
public sealed record DeleteRegValue(int Line, string KeyPath, string Name) : RegFileOperation(Line, KeyPath);

/// <param name="Operations">In file order, including repeated writes and deletions; empty when any diagnostic exists.</param>
public sealed record RegFileParseResult(IReadOnlyList<RegFileOperation> Operations, IReadOnlyList<RegFileDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.Count == 0;
}

public static class RegFileChanges
{
    /// <summary>The typed changes of a valid parse, in file order.</summary>
    public static IReadOnlyList<ImageChange> From(RegFileParseResult parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        if (!parsed.IsValid)
        {
            throw new ArgumentException("Only a valid .reg file can become changes.", nameof(parsed));
        }

        return [.. parsed.Operations.Select<RegFileOperation, ImageChange>(op => op switch
        {
            CreateRegKey key => new CreateRegistryKey(key.KeyPath),
            DeleteRegKey key => new DeleteRegistryKey(key.KeyPath),
            SetRegValue set => new SetRegistryValue(set.KeyPath, set.Name, set.Data),
            DeleteRegValue delete => new DeleteRegistryValue(delete.KeyPath, delete.Name),
            _ => throw new NotSupportedException(op.GetType().Name),
        })];
    }
}
