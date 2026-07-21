// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Tests.Common.Parity;

/// <summary>
/// Resolves each parity row's effective reusable assertion/helper entry point into exactly one unambiguous
/// <see cref="EntryPointKind"/>. The catalog records the fixture and reusable assertion/helper entry point
/// separately from the per-engine test locations: a row is <see cref="EntryPointKind.Direct"/> when it names
/// its own provider-neutral shared contract, <see cref="EntryPointKind.Inherited"/> when it explicitly defers to
/// another scenario's shared contract through <see cref="ParityScenario.CoveredByScenarioId"/> at the same
/// production boundary (a supporting-smoke deferral), and <see cref="EntryPointKind.ProviderSpecific"/> when no
/// shared contract applies and its existing per-engine or unit test locations are the effective entry points
/// (justified by a recorded rationale).
///
/// Inheritance is only ever explicit. Belonging to a canonical family (a shared id prefix, see
/// <c>ParityScenarioCatalog.CanonicalIdOf</c>) is used for grouping and naming validation, not contract
/// resolution: sharing a production boundary with the family does not imply running the family's assertion
/// helpers, so a variant that shares a boundary but exercises different helpers would otherwise silently
/// advertise the wrong reusable contract. Every ordinary variant therefore names its own
/// <see cref="ParityScenario.SharedEntryPoint"/> (or a provider-specific rationale); a variant that records
/// neither, and no <see cref="ParityScenario.CoveredByScenarioId"/> deferral, resolves to <c>null</c>
/// (unresolved) rather than inheriting a family contract by boundary alone.
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
            if (
                covered is not null
                && covered.Boundary == scenario.Boundary
                && !string.IsNullOrWhiteSpace(covered.SharedEntryPoint)
            )
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

        // Belonging to a canonical family (a shared id prefix) does not resolve a contract: sharing a production
        // boundary with the family does not imply running the family's assertion helpers, so a variant must name
        // its own SharedEntryPoint or defer explicitly through CoveredByScenarioId above. A variant that records
        // neither falls through to the provider-specific branch or resolves unresolved (null) below.
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
