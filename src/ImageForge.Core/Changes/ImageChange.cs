namespace ImageForge.Core.Changes;

/// <summary>Registry value types, numbered as the Win32 REG_* constants.</summary>
public enum RegistryValueType
{
    /// <summary>REG_SZ.</summary>
    Sz = 1,
}

public sealed record RegistryValue(RegistryValueType Type, string Data)
{
    public static RegistryValue Sz(string data) => new(RegistryValueType.Sz, data);
}

/// <summary>A typed change requested for an offline image. Commands and arbitrary paths are not representable.</summary>
public abstract record ImageChange;

/// <param name="ValueName">Empty string addresses the default value.</param>
public sealed record SetRegistryValue(string KeyPath, string ValueName, RegistryValue Value) : ImageChange;

public static class ValidationMarker
{
    /// <summary>The M1 test operation; intended for the validation fixture only.</summary>
    public static SetRegistryValue M1 { get; } =
        new(@"HKLM\SOFTWARE\ImageForge\Validation", "Milestone", RegistryValue.Sz("M1"));
}
