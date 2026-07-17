// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Tests.Common.Parity;

/// <summary>
/// Resolves each parity row's effective reusable assertion/helper entry point into exactly one unambiguous
/// <see cref="EntryPointKind"/>. The catalog records the fixture and reusable assertion/helper entry point
/// separately from the per-engine test locations: a row is <see cref="EntryPointKind.Direct"/> when it names
/// its own provider-neutral shared contract, <see cref="EntryPointKind.Inherited"/> when it reuses the shared
/// contract of the scenario it defers to or of its canonical family, and
/// <see cref="EntryPointKind.ProviderSpecific"/> when no shared contract applies and its existing per-engine or
/// unit test locations are the effective entry points (justified by a recorded rationale).
/// </summary>
public static class ParityEntryPointResolution
{
    /// <summary>
    /// Resolves the effective entry point for <paramref name="scenario"/> against the full catalog, or
    /// <c>null</c> when the row records no direct, inherited, or provider-specific entry point.
    /// </summary>
    public static EffectiveEntryPoint? ResolveEffectiveEntryPoint(ParityScenario scenario) =>
        ResolveEffectiveEntryPoint(scenario, ParityScenarioCatalog.All);

    /// <summary>
    /// Resolves the effective entry point for <paramref name="scenario"/>, resolving inherited contracts
    /// within <paramref name="catalog"/> so the invariants stay testable with a synthetic catalog. Returns
    /// <c>null</c> when the row records no direct, inherited, or provider-specific entry point.
    /// </summary>
    public static EffectiveEntryPoint? ResolveEffectiveEntryPoint(
        ParityScenario scenario,
        IReadOnlyList<ParityScenario> catalog
    )
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!string.IsNullOrWhiteSpace(scenario.SharedEntryPoint))
        {
            return new EffectiveEntryPoint(
                EntryPointKind.Direct,
                scenario.SharedEntryPoint,
                null,
                [],
                [],
                []
            );
        }

        if (!string.IsNullOrWhiteSpace(scenario.CoveredByScenarioId))
        {
            ParityScenario? covered = FindById(catalog, scenario.CoveredByScenarioId);
            if (covered is not null && !string.IsNullOrWhiteSpace(covered.SharedEntryPoint))
            {
                return new EffectiveEntryPoint(
                    EntryPointKind.Inherited,
                    covered.SharedEntryPoint,
                    covered.Id,
                    [],
                    [],
                    []
                );
            }
        }

        string canonicalId = ParityScenarioCatalog.CanonicalIdOf(scenario.Id);
        if (!string.Equals(canonicalId, scenario.Id, StringComparison.Ordinal))
        {
            ParityScenario? family = FindById(catalog, canonicalId);
            if (family is not null && !string.IsNullOrWhiteSpace(family.SharedEntryPoint))
            {
                return new EffectiveEntryPoint(
                    EntryPointKind.Inherited,
                    family.SharedEntryPoint,
                    family.Id,
                    [],
                    [],
                    []
                );
            }
        }

        bool hasLocation =
            !scenario.PgsqlLocations.IsDefaultOrEmpty
            || !scenario.MssqlLocations.IsDefaultOrEmpty
            || !scenario.UnitLocations.IsDefaultOrEmpty;

        if (hasLocation && !string.IsNullOrWhiteSpace(scenario.ProviderSpecificEntryPointRationale))
        {
            return new EffectiveEntryPoint(
                EntryPointKind.ProviderSpecific,
                null,
                null,
                scenario.PgsqlLocations,
                scenario.MssqlLocations,
                scenario.UnitLocations
            );
        }

        return null;
    }

    // Returns the target only when exactly one row carries the id. A malformed catalog with a duplicated
    // inheritance target must not throw out of the invariant validator, so zero or multiple matches resolve to
    // null (treated as "not inherited") and let the duplicate-id invariant report the malformed catalog.
    private static ParityScenario? FindById(IReadOnlyList<ParityScenario> catalog, string id)
    {
        ParityScenario? match = null;

        foreach (ParityScenario scenario in catalog)
        {
            if (!string.Equals(scenario.Id, id, StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = scenario;
        }

        return match;
    }
}
