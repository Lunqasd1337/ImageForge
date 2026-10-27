namespace ImageForge.Core.Safety;

public enum WriteScopeViolation
{
    None,
    NotAbsolute,
    UnsupportedPathForm,
    InvalidSegment,
    OutsideScope,
}

public readonly record struct WriteScopeCheck(WriteScopeViolation Violation, string? NormalizedPath)
{
    public bool IsAllowed => Violation == WriteScopeViolation.None;
}

/// <summary>
/// Lexical write boundary: a candidate path is allowed only if it normalizes to the root or below it.
/// The engine must additionally verify reparse points and file identity before writing.
/// </summary>
public sealed class WriteScope
{
    private WriteScope(string root) => Root = root;

    public string Root { get; }

    public static WriteScope Create(string rootPath)
    {
        var violation = WindowsPath.TryNormalize(rootPath, out var root);
        if (violation != WriteScopeViolation.None)
        {
            throw new ArgumentException($"Write scope root is not usable: {violation}.", nameof(rootPath));
        }

        if (root.Length <= 3)
        {
            throw new ArgumentException("Write scope root cannot be a drive root.", nameof(rootPath));
        }

        return new WriteScope(root);
    }

    public WriteScopeCheck Check(string candidate)
    {
        var violation = WindowsPath.TryNormalize(candidate, out var normalized);
        if (violation != WriteScopeViolation.None)
        {
            return new WriteScopeCheck(violation, null);
        }

        return WindowsPath.IsSameOrUnder(normalized, Root)
            ? new WriteScopeCheck(WriteScopeViolation.None, normalized)
            : new WriteScopeCheck(WriteScopeViolation.OutsideScope, null);
    }
}
