using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ImageForge.Core.Changes;
using ImageForge.Core.Registry;

namespace ImageForge.Core.Planning;

public interface IPipelinePlanner
{
    PlanResult Plan(PlanRequest request);
}

/// <summary>
/// Turns a typed change set into an executable plan. Any diagnostic blocks the whole plan.
/// Mount/unmount are workspace lifecycle operations and are not part of the plan.
/// </summary>
public sealed class PipelinePlanner(IRegistryPathMapper mapper, Func<Guid> newPlanId) : IPipelinePlanner
{
    public const int MaxValueNameLength = 16383;

    public PlanResult Plan(PlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Changes.Count == 0)
        {
            return Fail(new PlanningDiagnostic(PlanningCodes.EmptyChangeSet, -1, "Нет изменений для применения."));
        }

        var diagnostics = new List<PlanningDiagnostic>();
        var changes = new List<RegistryWritePreview>();
        for (var i = 0; i < request.Changes.Count; i++)
        {
            if (PlanRegistryChange(request.Changes[i], i, diagnostics) is { } preview)
            {
                changes.Add(preview);
            }
        }

        if (diagnostics.Count > 0)
        {
            return new PlanResult(null, diagnostics);
        }

        // One load/unload per hive; within a hive the requested order is kept, including repeats and deletions.
        var steps = new List<PlannedStep>();
        foreach (var hive in changes.GroupBy(c => c.Hive))
        {
            steps.Add(new LoadOfflineHive(hive.Key, hive.First().HiveFile));
            steps.AddRange(hive.Select(ToStep));
            steps.Add(new UnloadOfflineHive(hive.Key));
        }

        var plan = new ExecutionPlan(newPlanId(), request.WorkspaceId, request.WorkspaceRevision, request.ImageId, steps, changes, string.Empty);
        return new PlanResult(plan with { Fingerprint = PlanFingerprint.Compute(plan) }, []);
    }

    private static PlannedStep ToStep(RegistryWritePreview c) => c.Kind switch
    {
        RegistryChangeKind.SetValue => new SetOfflineRegistryValue(c.Hive, c.SubKey, c.ValueName!, c.NewValue!, c.UnderCurrentControlSet),
        RegistryChangeKind.DeleteValue => new DeleteOfflineRegistryValue(c.Hive, c.SubKey, c.ValueName!, c.UnderCurrentControlSet),
        RegistryChangeKind.CreateKey => new CreateOfflineRegistryKey(c.Hive, c.SubKey, c.UnderCurrentControlSet),
        RegistryChangeKind.DeleteKey => new DeleteOfflineRegistryKey(c.Hive, c.SubKey, c.UnderCurrentControlSet),
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    private RegistryWritePreview? PlanRegistryChange(ImageChange change, int index, List<PlanningDiagnostic> diagnostics)
    {
        var (keyPath, kind, valueName, value) = change switch
        {
            SetRegistryValue set => (set.KeyPath, RegistryChangeKind.SetValue, set.ValueName, set.Value),
            DeleteRegistryValue delete => (delete.KeyPath, RegistryChangeKind.DeleteValue, delete.ValueName, null),
            CreateRegistryKey key => (key.KeyPath, RegistryChangeKind.CreateKey, null, null),
            DeleteRegistryKey key => (key.KeyPath, RegistryChangeKind.DeleteKey, (string?)null, (RegistryValue?)null),
            _ => (null, default, null, null),
        };
        if (keyPath is null)
        {
            diagnostics.Add(new PlanningDiagnostic(PlanningCodes.UnsupportedChange, index, "Тип изменения не поддерживается."));
            return null;
        }

        var mapping = mapper.Map(keyPath);
        if (mapping.Location is not { } location)
        {
            diagnostics.Add(new PlanningDiagnostic(CodeFor(mapping.Error), index, $"Путь реестра не может быть применён: {keyPath}"));
            return null;
        }

        if (kind == RegistryChangeKind.DeleteKey && location.SubKey.Length == 0)
        {
            diagnostics.Add(new PlanningDiagnostic(PlanningCodes.RegistryKeyDeleteRoot, index, $"Нельзя удалить корень куста: {keyPath}"));
            return null;
        }

        if (valueName is not null && (valueName.Length > MaxValueNameLength || valueName.Contains('\0', StringComparison.Ordinal)))
        {
            diagnostics.Add(new PlanningDiagnostic(PlanningCodes.RegistryValueInvalid, index, $"Недопустимое имя значения в {keyPath}"));
            return null;
        }

        if (value is not null && !IsWellFormed(value))
        {
            diagnostics.Add(new PlanningDiagnostic(PlanningCodes.RegistryValueInvalid, index, $"Недопустимое значение реестра в {keyPath}"));
            return null;
        }

        return new RegistryWritePreview(
            keyPath, location.Hive, location.HiveFile, location.SubKey, valueName, value, kind,
            location.UnderCurrentControlSet, location.ClassesRootPolicy);
    }

    /// <summary>A string value must not hide data after an embedded nul; other types keep their bytes as given.</summary>
    private static bool IsWellFormed(RegistryValue value) => value.Type switch
    {
        RegistryValueType.Sz or RegistryValueType.ExpandSz => value.Text is { } text && !text.Contains('\0', StringComparison.Ordinal),
        > RegistryValueType.Qword => false,
        _ => true,
    };

    private static string CodeFor(RegistryMappingError error) => error switch
    {
        RegistryMappingError.ForbiddenRoot => PlanningCodes.RegistryRootForbidden,
        RegistryMappingError.NotSupportedYet => PlanningCodes.RegistryRootNotSupportedYet,
        RegistryMappingError.UnknownRoot => PlanningCodes.RegistryRootUnknown,
        _ => PlanningCodes.RegistryPathMalformed,
    };

    private static PlanResult Fail(PlanningDiagnostic diagnostic) => new(null, [diagnostic]);
}

/// <summary>SHA-256 over a length-prefixed canonical form of everything the plan will do. PlanId is excluded.</summary>
public static class PlanFingerprint
{
    private const int FormatVersion = 2;

    public static string Compute(ExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var builder = new StringBuilder();
        Append(builder, FormatVersion);
        Append(builder, plan.WorkspaceId);
        Append(builder, plan.WorkspaceRevision);
        Append(builder, plan.ImageId);
        foreach (var step in plan.Steps)
        {
            switch (step)
            {
                case LoadOfflineHive load:
                    Append(builder, "load", load.Hive, load.HiveFile);
                    break;
                case SetOfflineRegistryValue set:
                    Append(builder, "set", set.Hive, set.UnderCurrentControlSet, set.SubKey, set.ValueName, (uint)set.Value.Type, Convert.ToHexString(set.Value.Bytes));
                    break;
                case DeleteOfflineRegistryValue delete:
                    Append(builder, "delete-value", delete.Hive, delete.UnderCurrentControlSet, delete.SubKey, delete.ValueName);
                    break;
                case CreateOfflineRegistryKey key:
                    Append(builder, "create-key", key.Hive, key.UnderCurrentControlSet, key.SubKey);
                    break;
                case DeleteOfflineRegistryKey key:
                    Append(builder, "delete-key", key.Hive, key.UnderCurrentControlSet, key.SubKey);
                    break;
                case UnloadOfflineHive unload:
                    Append(builder, "unload", unload.Hive);
                    break;
                default:
                    throw new NotSupportedException($"Unknown plan step {step.GetType().Name}.");
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void Append(StringBuilder builder, params object[] fields)
    {
        foreach (var field in fields)
        {
            var text = Convert.ToString(field, CultureInfo.InvariantCulture) ?? string.Empty;
            builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append(';');
        }
    }
}
