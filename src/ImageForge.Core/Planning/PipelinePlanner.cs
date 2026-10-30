using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ImageForge.Core.Appx;
using ImageForge.Core.Changes;
using ImageForge.Core.Drivers;
using ImageForge.Core.Features;
using ImageForge.Core.Registry;
using ImageForge.Core.Updates;

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
        var requested = new List<(AppxRemoval Removal, int Index)>();
        var featureChanges = new List<(ImageChange Change, int Index)>();
        var capabilityChanges = new List<(ImageChange Change, int Index)>();
        var driverChanges = new List<(ImageChange Change, int Index)>();
        var updateChanges = new List<(string FileName, int Index)>();
        var winReChanges = new List<(string FileName, int Index)>();
        CleanupPlan? cleanup = null;
        for (var i = 0; i < request.Changes.Count; i++)
        {
            if (request.Changes[i] is EnableWindowsFeature or DisableWindowsFeature)
            {
                featureChanges.Add((request.Changes[i], i));
            }
            else if (request.Changes[i] is RemoveCapability or AddCapability)
            {
                capabilityChanges.Add((request.Changes[i], i));
            }
            else if (request.Changes[i] is AddDriverPackage or RemoveDriverPackage)
            {
                driverChanges.Add((request.Changes[i], i));
            }
            else if (request.Changes[i] is AddUpdatePackage update)
            {
                updateChanges.Add((update.FileName, i));
            }
            else if (request.Changes[i] is AddWinReUpdatePackage winReUpdate)
            {
                winReChanges.Add((winReUpdate.FileName, i));
            }
            else if (request.Changes[i] is CleanupComponentStore clean)
            {
                // Asked for twice, it runs once; ResetBase if either asked for it.
                cleanup = PlanCleanup(clean.ResetBase || cleanup?.ResetBase == true);
            }
            else if (request.Changes[i] is RemoveProvisionedAppx remove)
            {
                foreach (var removal in PlanAppxRemoval(remove, request.Appx, i, diagnostics))
                {
                    if (!requested.Any(r => r.Removal.Package == removal.Package))
                    {
                        requested.Add((removal, i));
                    }
                }
            }
            else if (PlanRegistryChange(request.Changes[i], i, diagnostics) is { } preview)
            {
                changes.Add(preview);
            }
        }

        var removals = diagnostics.Count == 0 && requested.Count > 0 ? OrderAppxRemovals(requested, request.Appx!, diagnostics) : [];
        var features = featureChanges.Count > 0 ? PlanFeatures(featureChanges, request, diagnostics) : null;
        var capabilities = capabilityChanges.Count > 0 ? PlanCapabilities(capabilityChanges, request, diagnostics) : null;
        var drivers = driverChanges.Count > 0 ? PlanDrivers(driverChanges, request, diagnostics) : null;
        var updates = updateChanges.Count > 0 ? PlanUpdates(updateChanges, request.UpdateSource, diagnostics) : null;
        var winRe = winReChanges.Count > 0 ? PlanWinRe(winReChanges, request.UpdateSource, diagnostics) : null;
        if (diagnostics.Count == 0 && changes.Count == 0 && removals.Count == 0 && features is null && capabilities is null && drivers is null && updates is null
            && cleanup is null && winRe is null)
        {
            diagnostics.Add(new PlanningDiagnostic(PlanningCodes.EmptyChangeSet, -1, "Компоненты уже в нужном состоянии; изменять нечего."));
        }

        if (diagnostics.Count > 0)
        {
            return new PlanResult(null, diagnostics);
        }

        // HKCU\Software\Classes has no hive in the image; it is delivered at first logon, and the file goes in first.
        var steps = new List<PlannedStep>();
        var userClasses = changes.Where(c => c.Hive == OfflineHive.DefaultUserClasses).Select(c => c with { FirstLogon = true }).ToList();
        changes = [.. changes.Where(c => c.Hive != OfflineHive.DefaultUserClasses)];
        if (userClasses.Count > 0)
        {
            var package = FirstLogonDelivery.UserClasses(userClasses);
            steps.Add(new WriteImageFile(package.FileRelativePath, package.Content));
            changes.AddRange(package.Registration.Select(r => new RegistryWritePreview(
                @"HKLM\SOFTWARE\" + package.ComponentKey, OfflineHive.Software, RegistryPathMapper.SoftwareHiveFile,
                package.ComponentKey, r.Name, r.Value)));
        }

        // One load/unload per hive; within a hive the requested order is kept, including repeats and deletions.
        foreach (var hive in changes.GroupBy(c => c.Hive))
        {
            steps.Add(new LoadOfflineHive(hive.Key, hive.First().HiveFile));
            steps.AddRange(hive.Select(ToStep));
            steps.Add(new UnloadOfflineHive(hive.Key));
        }

        var plan = new ExecutionPlan(
            newPlanId(), request.WorkspaceId, request.WorkspaceRevision, request.ImageId, steps, [.. userClasses, .. changes], string.Empty)
        {
            AppxRemovals = removals,
            AppxInventory = removals.Count > 0 ? request.Appx!.Fingerprint : null,
            Features = features,
            Capabilities = capabilities,
            Drivers = drivers,
            Updates = updates,
            Cleanup = cleanup,
            WinRe = winRe,
        };
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

    private static IEnumerable<AppxRemoval> PlanAppxRemoval(RemoveProvisionedAppx remove, AppxInventory? inventory, int index, List<PlanningDiagnostic> diagnostics)
    {
        if (inventory is null || inventory.Problems.Count > 0)
        {
            diagnostics.Add(new PlanningDiagnostic(
                PlanningCodes.AppxInventoryMissing, index, "Список приложений образа не прочитан или прочитан с ошибками; удаление невозможно."));
            return [];
        }

        var matches = inventory.Packages
            .Where(p => string.Equals(p.Identity.FamilyName, remove.FamilyName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            diagnostics.Add(new PlanningDiagnostic(PlanningCodes.AppxNotProvisioned, index, $"В образе нет приложения {remove.FamilyName}."));
            return [];
        }

        return matches.Select(m => new AppxRemoval(m.Identity, m.DisplayName, AppxWarnings.For(m.Identity)));
    }

    /// <summary>
    /// Checks the removals against what the remaining packages and the inbox apps need (plan, section 8) and orders
    /// them so that a package goes before the packages it depends on. Anything that cannot be resolved blocks.
    /// </summary>
    private static List<AppxRemoval> OrderAppxRemovals(
        List<(AppxRemoval Removal, int Index)> removals, AppxInventory inventory, List<PlanningDiagnostic> diagnostics)
    {
        var removed = removals.Select(r => r.Removal.Package.FullName).ToHashSet(StringComparer.Ordinal);
        var byName = inventory.Packages.ToDictionary(p => p.PackageName, StringComparer.Ordinal);
        var kept = inventory.Packages.Where(p => !removed.Contains(p.PackageName)).ToList();
        var blocked = false;
        foreach (var (removal, index) in removals)
        {
            var package = byName[removal.Package.FullName];
            var family = removal.Package.FamilyName;
            if (package.Facts is not { } facts)
            {
                diagnostics.Add(new PlanningDiagnostic(
                    PlanningCodes.AppxDependenciesUnknown, index, $"Зависимости {package.DisplayName} не прочитаны ({package.FactsProblem}); удаление заблокировано."));
                blocked = true;
                continue;
            }

            var dependents = kept.Where(k => k.Facts?.DependsOn?.Contains(family, StringComparer.OrdinalIgnoreCase) == true).Select(k => k.DisplayName).ToList();
            if (dependents.Count > 0)
            {
                diagnostics.Add(new PlanningDiagnostic(
                    PlanningCodes.AppxRequiredByKept, index, $"{package.DisplayName} нужна приложениям, которые остаются: {string.Join(", ", dependents)}. Отметьте и их или оставьте {package.DisplayName}."));
                blocked = true;
            }

            if (inventory.InboxDependencies?.Contains(family, StringComparer.OrdinalIgnoreCase) == true)
            {
                diagnostics.Add(new PlanningDiagnostic(
                    PlanningCodes.AppxRequiredByInbox, index, $"{package.DisplayName} нужна встроенным приложениям Windows; её нельзя удалить."));
                blocked = true;
            }

            // A framework is what unread manifests would most likely depend on, so it waits until all are known.
            if (facts.IsFramework && (inventory.InboxDependencies is null || kept.Any(k => k.Facts?.DependsOn is null)))
            {
                diagnostics.Add(new PlanningDiagnostic(
                    PlanningCodes.AppxFrameworkBlocked, index, $"{package.DisplayName} — библиотека, а зависимости части приложений не прочитаны; удаление заблокировано."));
                blocked = true;
            }
        }

        if (blocked)
        {
            return [];
        }

        // Kahn's algorithm in request order: a removal waits while a removal that depends on it is still pending.
        var pending = removals.Select(r => r.Removal).ToList();
        var ordered = new List<AppxRemoval>();
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(candidate => !pending.Any(other =>
                other != candidate && byName[other.Package.FullName].Facts!.DependsOn?.Contains(candidate.Package.FamilyName, StringComparer.OrdinalIgnoreCase) == true));
            if (next is null)
            {
                diagnostics.Add(new PlanningDiagnostic(
                    PlanningCodes.AppxDependencyCycle, -1, $"Приложения зависят друг от друга по кругу: {string.Join(", ", pending.Select(p => p.DisplayName))}; порядок удаления не определить."));
                return [];
            }

            ordered.Add(next);
            pending.Remove(next);
        }

        return ordered;
    }

    /// <summary>
    /// Works out each requested feature change with its parents or dependents (plan, section 10) and merges them into
    /// one disable and one enable, in order. A feature that would be both turned on and off blocks.
    /// </summary>
    private static FeaturePlan? PlanFeatures(List<(ImageChange Change, int Index)> changes, PlanRequest request, List<PlanningDiagnostic> diagnostics)
    {
        if (request.Features is not { Problems.Count: 0, Parents: { } parents } inventory)
        {
            diagnostics.Add(new PlanningDiagnostic(
                PlanningCodes.FeatureInventoryMissing, changes[0].Index, "Список компонентов образа или их зависимости не прочитаны; изменить компоненты нельзя."));
            return null;
        }

        var enable = new List<string>();
        var disable = new List<string>();
        var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var needsSource = new List<(string Feature, int Index)>();
        var blocked = false;
        foreach (var (change, index) in changes)
        {
            var (name, turnOn) = change switch
            {
                EnableWindowsFeature e => (e.Name, true),
                DisableWindowsFeature d => (d.Name, false),
                _ => throw new ArgumentOutOfRangeException(nameof(changes)),
            };
            var result = turnOn ? FeatureDependencies.Enable(inventory, parents, name) : FeatureDependencies.Disable(inventory, parents, name);
            foreach (var problem in result.Problems)
            {
                diagnostics.Add(new PlanningDiagnostic(PlanningCodes.FeatureBlocked, index, $"Компонент {name}: {problem}"));
                blocked = true;
            }

            asked.Add(name);
            var target = turnOn ? enable : disable;
            target.AddRange(result.Features.Where(f => !target.Contains(f, StringComparer.OrdinalIgnoreCase)));
            needsSource.AddRange(result.NeedsSource.Select(f => (f, index)));
        }

        // A feature to enable needs all its ancestors on, including those already on that a disable turns off.
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(enable);
        while (queue.TryDequeue(out var feature))
        {
            if (needed.Add(feature))
            {
                foreach (var parent in parents.GetValueOrDefault(feature, []))
                {
                    queue.Enqueue(parent);
                }
            }
        }

        foreach (var conflict in disable.Where(needed.Contains))
        {
            diagnostics.Add(new PlanningDiagnostic(
                PlanningCodes.FeatureConflict, -1, $"Компонент {conflict} нужно и включить, и выключить; уберите одно из изменений."));
            blocked = true;
        }

        foreach (var (feature, index) in needsSource.DistinctBy(n => n.Feature, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(feature, FeatureSources.NetFx3, StringComparison.OrdinalIgnoreCase) || !request.MediaHasNetFx3)
            {
                diagnostics.Add(new PlanningDiagnostic(
                    PlanningCodes.FeatureSourceMissing, index,
                    $"Файлов компонента {feature} нет в образе, а в sources\\sxs дистрибутива нет подходящего пакета; загрузка из Windows Update не используется."));
                blocked = true;
            }
        }

        return blocked || (enable.Count == 0 && disable.Count == 0)
            ? null
            : new FeaturePlan(disable, enable, [.. disable.Concat(enable).Where(f => !asked.Contains(f))], needsSource.Count > 0, inventory.Fingerprint);
    }

    /// <summary>
    /// Removals need an installed capability; additions one listed as Not Present and a checked Features on Demand
    /// folder. What DISM adds along with it (language dependencies) is not known in advance; the run shows it.
    /// </summary>
    private static CapabilityPlan? PlanCapabilities(
        List<(ImageChange Change, int Index)> changes, PlanRequest request, List<PlanningDiagnostic> diagnostics)
    {
        if (request.Capabilities is not { Problems.Count: 0 } inventory)
        {
            diagnostics.Add(new PlanningDiagnostic(
                PlanningCodes.CapabilityInventoryMissing, changes[0].Index, "Список дополнительных компонентов (Capabilities) не прочитан; изменить их нельзя."));
            return null;
        }

        var remove = new List<CapabilityIdentity>();
        var add = new List<CapabilityIdentity>();
        foreach (var (change, index) in changes)
        {
            var (name, adding) = change switch
            {
                AddCapability a => (a.Name, true),
                RemoveCapability r => (r.Name, false),
                _ => throw new ArgumentOutOfRangeException(nameof(changes)),
            };
            var capability = inventory.Capabilities.FirstOrDefault(c => string.Equals(c.Identity.FullName, name, StringComparison.OrdinalIgnoreCase));
            if (!adding && capability is not { State: CapabilityState.Installed })
            {
                diagnostics.Add(new PlanningDiagnostic(
                    PlanningCodes.CapabilityNotInstalled, index, $"Дополнительный компонент {name} не установлен в образе."));
            }
            else if (adding && capability is not { State: CapabilityState.NotPresent })
            {
                diagnostics.Add(new PlanningDiagnostic(
                    PlanningCodes.CapabilityNotAvailable, index, capability is null
                        ? $"Дополнительного компонента {name} нет в списке образа."
                        : $"Дополнительный компонент {name} уже в состоянии «{capability.RawState}»; добавить нельзя."));
            }
            else if (adding && request.FodSource is null)
            {
                diagnostics.Add(new PlanningDiagnostic(
                    PlanningCodes.CapabilitySourceMissing, index, request.FodSourceProblem is { } problem
                        ? $"Папка Features on Demand не подходит: {problem}"
                        : "Для добавления нужна папка Features on Demand (LanguagesAndOptionalFeatures с ISO); она не указана."));
            }
            else
            {
                var list = adding ? add : remove;
                if (!list.Contains(capability!.Identity))
                {
                    list.Add(capability.Identity);
                }
            }
        }

        return diagnostics.Count == 0 && remove.Count + add.Count > 0
            ? new CapabilityPlan(remove, inventory.Fingerprint) { Add = add, Source = add.Count > 0 ? request.FodSource : null }
            : null;
    }

    /// <summary>
    /// Removals need a third-party driver of the image, and consent for a boot-critical one; additions a package of the
    /// scanned folder without problems, and ForceUnsigned for one without a catalog. Packages with the same content are
    /// added once (plan, section 12).
    /// </summary>
    private static DriverPlan? PlanDrivers(List<(ImageChange Change, int Index)> changes, PlanRequest request, List<PlanningDiagnostic> diagnostics)
    {
        if (request.Drivers is not { Problems.Count: 0 } inventory)
        {
            diagnostics.Add(new PlanningDiagnostic(
                PlanningCodes.DriverInventoryMissing, changes[0].Index, "Список драйверов образа не прочитан; изменить драйверы нельзя."));
            return null;
        }

        var count = diagnostics.Count;
        var remove = new List<DriverRemoval>();
        var add = new List<DriverAddition>();
        var warnings = new List<string>();
        foreach (var (change, index) in changes)
        {
            if (change is RemoveDriverPackage removal)
            {
                var driver = inventory.Drivers.FirstOrDefault(d => string.Equals(d.PublishedName, removal.PublishedName, StringComparison.OrdinalIgnoreCase));
                if (driver is null)
                {
                    diagnostics.Add(new PlanningDiagnostic(
                        PlanningCodes.DriverNotInstalled, index, $"В образе нет стороннего драйвера {removal.PublishedName}."));
                }
                else if (driver.BootCritical && !removal.AllowBootCritical)
                {
                    diagnostics.Add(new PlanningDiagnostic(
                        PlanningCodes.DriverBootCritical, index, $"Драйвер {driver.PublishedName} ({driver.InfName}) нужен для загрузки; удалить его можно только с отдельным подтверждением."));
                }
                else if (!remove.Any(r => r.PublishedName == driver.PublishedName))
                {
                    remove.Add(new DriverRemoval(driver.PublishedName, Describe(driver), driver.BootCritical));
                    if (driver.BootCritical)
                    {
                        warnings.Add($"Удаляется драйвер, нужный для загрузки: {driver.PublishedName} ({driver.InfName}). Без него образ может не загрузиться.");
                    }
                }
            }
            else if (change is AddDriverPackage addition && PlanDriverAddition(addition, index, request.DriverSource, diagnostics) is { } scanned)
            {
                var hash = scanned.ContentHash!;
                var same = add.FindIndex(a => a.ContentHash == hash);
                var unsigned = scanned.Package.IsUnsigned;
                if (same >= 0)
                {
                    var first = add[same];
                    add[same] = first with
                    {
                        ForceUnsigned = first.ForceUnsigned || (unsigned && addition.ForceUnsigned),
                        SameContent = string.Equals(first.InfPath, scanned.Package.InfPath, StringComparison.OrdinalIgnoreCase) || first.SameContent.Contains(scanned.Package.InfPath, StringComparer.OrdinalIgnoreCase)
                            ? first.SameContent
                            : [.. first.SameContent, scanned.Package.InfPath],
                    };
                    continue;
                }

                var inf = scanned.Package.Inf!;
                add.Add(new DriverAddition(scanned.Package.InfPath, hash, unsigned, Describe(scanned.Package), []));
                if (unsigned)
                {
                    warnings.Add($"Пакет {scanned.Package.InfPath} без подписи добавляется с ForceUnsigned: DISM не проверит подпись, и в установленной системе драйвер может не загрузиться.");
                }

                var name = scanned.Package.InfPath[(scanned.Package.InfPath.LastIndexOf('\\') + 1)..];
                foreach (var present in inventory.Drivers.Where(d => string.Equals(d.InfName, name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(d.Version, inf.DriverVersion, StringComparison.OrdinalIgnoreCase)))
                {
                    warnings.Add($"В образе уже есть {present.PublishedName} ({present.InfName} {present.Version}); такой же пакет DISM не добавит второй раз.");
                }
            }
        }

        return diagnostics.Count == count && remove.Count + add.Count > 0
            ? new DriverPlan(remove, add, add.Count > 0 ? request.DriverSource!.Folder : null, inventory.Fingerprint, warnings.Distinct().ToList())
            : null;
    }

    /// <summary>
    /// Updates come only from the scanned folder, whole and with a hash; the same file content is added once. Servicing
    /// stack packages go first. DISM checks the signature and applicability when the run adds each one.
    /// </summary>
    private static UpdatePlan? PlanUpdates(List<(string FileName, int Index)> changes, UpdateFolder? source, List<PlanningDiagnostic> diagnostics)
    {
        if (CollectUpdates(changes, source, diagnostics) is not { } add)
        {
            return null;
        }

        var warnings = new List<string>();
        var cumulative = add.Where(a => !a.ServicingStack && a.Package.Kind == UpdateKind.Msu && a.Package.Kb is not null)
            .Select(a => $"KB{a.Package.Kb}").Distinct().ToList();
        if (cumulative.Count > 1)
        {
            warnings.Add($"Выбрано несколько MSU ({string.Join(", ", cumulative)}): DISM поставит каждый по очереди, а накопительному обновлению обычно достаточно последнего.");
        }

        return new UpdatePlan([.. add.Where(a => a.ServicingStack).Concat(add.Where(a => !a.ServicingStack)).Select(a => a.Addition)], source!.Folder, warnings);
    }

    /// <summary>
    /// The recipe checked in S2-TARGETS-INVENTORY: in WinRE a cumulative MSU installs only its servicing stack, so it goes
    /// first and the Safe OS Dynamic Update (a .cab) after it.
    /// </summary>
    private static WinRePlan? PlanWinRe(List<(string FileName, int Index)> changes, UpdateFolder? source, List<PlanningDiagnostic> diagnostics)
    {
        if (CollectUpdates(changes, source, diagnostics) is not { } add)
        {
            return null;
        }

        var warnings = new List<string>();
        if (add.All(a => a.Package.Kind == UpdateKind.Msu))
        {
            warnings.Add("В WinRE накопительное обновление ставит только стек обслуживания; саму среду восстановления обновляет Safe OS Dynamic Update (.cab) того же месяца.");
        }

        return new WinRePlan(
            [.. add.Where(a => a.Package.Kind == UpdateKind.Msu).Concat(add.Where(a => a.Package.Kind != UpdateKind.Msu)).Select(a => a.Addition)],
            source!.Folder, warnings);
    }

    private static List<(UpdateAddition Addition, bool ServicingStack, UpdatePackageInfo Package)>? CollectUpdates(
        List<(string FileName, int Index)> changes, UpdateFolder? source, List<PlanningDiagnostic> diagnostics)
    {
        var count = diagnostics.Count;
        var add = new List<(UpdateAddition Addition, bool ServicingStack, UpdatePackageInfo Package)>();
        foreach (var (fileName, index) in changes)
        {
            if (source is null || source.Problem is not null)
            {
                diagnostics.Add(new PlanningDiagnostic(
                    PlanningCodes.UpdateSourceMissing, index, source?.Problem is { } problem
                        ? $"Папка обновлений не подходит: {problem}."
                        : "Папка обновлений не указана или не просканирована."));
                continue;
            }

            var scanned = source.Packages.FirstOrDefault(p => string.Equals(p.Package.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            if (scanned is null)
            {
                diagnostics.Add(new PlanningDiagnostic(PlanningCodes.UpdateSourceMissing, index, $"В папке обновлений нет {fileName}."));
                continue;
            }

            if (scanned.Package.Problems.Count > 0 || scanned.Sha256 is null)
            {
                var problems = scanned.Package.Problems.Count > 0 ? string.Join("; ", scanned.Package.Problems) : "файл не прочитан";
                diagnostics.Add(new PlanningDiagnostic(PlanningCodes.UpdatePackageInvalid, index, $"Пакет {fileName} нельзя добавить: {problems}."));
                continue;
            }

            var same = add.FindIndex(a => a.Addition.Sha256 == scanned.Sha256);
            if (same >= 0)
            {
                var first = add[same].Addition;
                if (!string.Equals(first.FileName, scanned.Package.FileName, StringComparison.OrdinalIgnoreCase)
                    && !first.SameContent.Contains(scanned.Package.FileName, StringComparer.OrdinalIgnoreCase))
                {
                    add[same] = add[same] with { Addition = first with { SameContent = [.. first.SameContent, scanned.Package.FileName] } };
                }

                continue;
            }

            add.Add((new UpdateAddition(scanned.Package.FileName, scanned.Sha256, Describe(scanned.Package), []), scanned.Package.HasServicingStack, scanned.Package));
        }

        return diagnostics.Count != count || add.Count == 0 ? null : add;
    }

    private static CleanupPlan PlanCleanup(bool resetBase) => new(resetBase, resetBase
        ? ["С /ResetBase вытесненные версии компонентов удаляются насовсем: обновления, уже стоящие в образе, нельзя будет удалить."]
        : []);

    private static string Describe(UpdatePackageInfo p) =>
        $"{p.FileName}: {(p.Kb is { } kb ? $"KB{kb}" : "без KB")}, {p.Kind.ToString().ToUpperInvariant()}" +
        $"{(p.Container == UpdateContainer.Wim ? " (WIM)" : string.Empty)}{(p.HasServicingStack ? ", стек обслуживания" : string.Empty)}";

    private static ScannedDriver? PlanDriverAddition(AddDriverPackage addition, int index, DriverFolder? source, List<PlanningDiagnostic> diagnostics)
    {
        if (source is null || source.Problem is not null)
        {
            diagnostics.Add(new PlanningDiagnostic(
                PlanningCodes.DriverSourceMissing, index, source?.Problem is { } problem
                    ? $"Папка драйверов не подходит: {problem}"
                    : "Для добавления драйверов нужна папка с пакетами; она не указана."));
            return null;
        }

        var scanned = source.Packages.FirstOrDefault(p => string.Equals(p.Package.InfPath, addition.InfPath, StringComparison.OrdinalIgnoreCase));
        if (scanned is null)
        {
            diagnostics.Add(new PlanningDiagnostic(PlanningCodes.DriverSourceMissing, index, $"В папке драйверов нет {addition.InfPath}."));
            return null;
        }

        if (scanned.Package.Inf is null || scanned.Package.Problems.Count > 0 || scanned.ContentHash is null)
        {
            var problems = scanned.Package.Problems.Count > 0 ? string.Join(" ", scanned.Package.Problems) : "файлы пакета не прочитаны.";
            diagnostics.Add(new PlanningDiagnostic(PlanningCodes.DriverPackageInvalid, index, $"Пакет {addition.InfPath} нельзя добавить: {problems}"));
            return null;
        }

        if (scanned.Package.IsUnsigned && !addition.ForceUnsigned)
        {
            diagnostics.Add(new PlanningDiagnostic(
                PlanningCodes.DriverUnsigned, index, $"У пакета {addition.InfPath} нет каталога (подписи); добавить его можно только с ForceUnsigned."));
            return null;
        }

        return scanned;
    }

    private static string Describe(InstalledDriver d) => $"{d.PublishedName}: {d.InfName}, {d.ClassName}, {d.Provider}, {d.Version}";

    private static string Describe(DriverPackageInfo p) => $"{p.InfPath}: {p.Inf!.Class ?? "-"}, {p.Inf.Provider ?? "-"}, {p.Inf.DriverVersion ?? "-"}";

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

        if (location.Hive == OfflineHive.DefaultUserClasses && !(FirstLogonDelivery.CanHold(location.SubKey) && FirstLogonDelivery.CanHold(valueName ?? string.Empty)))
        {
            diagnostics.Add(new PlanningDiagnostic(
                PlanningCodes.FirstLogonUnrepresentable, index, $"Перевод строки в имени нельзя передать через .reg для первого входа: {keyPath}"));
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
    private const int FormatVersion = 9;

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
                case WriteImageFile file:
                    Append(builder, "write-file", file.RelativePath, Convert.ToHexString(SHA256.HashData(file.Content)));
                    break;
                default:
                    throw new NotSupportedException($"Unknown plan step {step.GetType().Name}.");
            }
        }

        if (plan.AppxRemovals.Count > 0)
        {
            Append(builder, "appx-inventory", plan.AppxInventory ?? string.Empty);
            foreach (var removal in plan.AppxRemovals)
            {
                Append(builder, "remove-appx", removal.Package.FullName);
            }
        }

        if (plan.Capabilities is { } capabilities)
        {
            Append(builder, "capability-inventory", capabilities.Inventory);
            foreach (var capability in capabilities.Remove)
            {
                Append(builder, "remove-capability", capability.FullName);
            }

            foreach (var capability in capabilities.Add)
            {
                Append(builder, "add-capability", capability.FullName);
            }

            if (capabilities.Source is { } source)
            {
                Append(builder, "fod-source", source);
            }
        }

        if (plan.Features is { } features)
        {
            Append(builder, "feature-inventory", features.Inventory, features.UsesMediaSxs);
            foreach (var feature in features.Disable)
            {
                Append(builder, "disable-feature", feature);
            }

            foreach (var feature in features.Enable)
            {
                Append(builder, "enable-feature", feature);
            }
        }

        if (plan.Drivers is { } drivers)
        {
            Append(builder, "driver-inventory", drivers.Inventory, drivers.Source ?? string.Empty);
            foreach (var removal in drivers.Remove)
            {
                Append(builder, "remove-driver", removal.PublishedName);
            }

            foreach (var addition in drivers.Add)
            {
                Append(builder, "add-driver", addition.InfPath, addition.ContentHash, addition.ForceUnsigned);
            }
        }

        if (plan.Updates is { } updates)
        {
            Append(builder, "update-source", updates.Source);
            foreach (var addition in updates.Add)
            {
                Append(builder, "add-update", addition.FileName, addition.Sha256);
            }
        }

        if (plan.Cleanup is { } cleanup)
        {
            Append(builder, "cleanup", cleanup.ResetBase);
        }

        if (plan.WinRe is { } winRe)
        {
            Append(builder, "winre-source", winRe.Source);
            foreach (var addition in winRe.Add)
            {
                Append(builder, "winre-update", addition.FileName, addition.Sha256);
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
