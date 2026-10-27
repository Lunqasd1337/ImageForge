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
        var writes = new List<RegistryWritePreview>();
        for (var i = 0; i < request.Changes.Count; i++)
        {
            switch (request.Changes[i])
            {
                case SetRegistryValue set:
                    PlanRegistryWrite(set, i, writes, diagnostics);
                    break;
                default:
                    diagnostics.Add(new PlanningDiagnostic(PlanningCodes.UnsupportedChange, i, "Тип изменения не поддерживается."));
                    break;
            }
        }

        if (diagnostics.Count > 0)
        {
            return new PlanResult(null, diagnostics);
        }

        var steps = new List<PlannedStep>();
        foreach (var hive in writes.GroupBy(w => w.Hive))
        {
            steps.Add(new LoadOfflineHive(hive.Key, hive.First().HiveFile));
            steps.AddRange(hive.Select(w => new SetOfflineRegistryValue(w.Hive, w.SubKey, w.ValueName, w.NewValue)));
            steps.Add(new UnloadOfflineHive(hive.Key));
        }

        var plan = new ExecutionPlan(newPlanId(), request.WorkspaceId, request.WorkspaceRevision, request.ImageId, steps, writes, string.Empty);
        return new PlanResult(plan with { Fingerprint = PlanFingerprint.Compute(plan) }, []);
    }

    private void PlanRegistryWrite(SetRegistryValue set, int index, List<RegistryWritePreview> writes, List<PlanningDiagnostic> diagnostics)
    {
        var mapping = mapper.Map(set.KeyPath);
        if (mapping.Location is not { } location)
        {
            diagnostics.Add(new PlanningDiagnostic(CodeFor(mapping.Error), index, $"Путь реестра не может быть применён: {set.KeyPath}"));
            return;
        }

        if (set.ValueName.Length > MaxValueNameLength
            || set.ValueName.Contains('\0', StringComparison.Ordinal)
            || set.Value.Type != RegistryValueType.Sz
            || set.Value.Data.Contains('\0', StringComparison.Ordinal))
        {
            diagnostics.Add(new PlanningDiagnostic(PlanningCodes.RegistryValueInvalid, index, $"Недопустимое значение реестра в {set.KeyPath}"));
            return;
        }

        writes.Add(new RegistryWritePreview(set.KeyPath, location.Hive, location.HiveFile, location.SubKey, set.ValueName, set.Value));
    }

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
    private const int FormatVersion = 1;

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
                    Append(builder, "set", set.Hive, set.SubKey, set.ValueName, (int)set.Value.Type, set.Value.Data);
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
