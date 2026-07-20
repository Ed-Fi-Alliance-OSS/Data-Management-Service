// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.Tests.Common.Parity;

/// <summary>
/// Pure structural invariants over a parity catalog. Returns a human-readable message per
/// violation (empty when the catalog is well-formed). No reflection or database access, so it
/// runs in the unit lane and is testable with synthetic catalogs. The set of canonical no-profile
/// ids is passed in so a supporting-smoke deferral can be checked for exact canonical identity.
/// </summary>
public static class ParityCatalogInvariants
{
    /// <summary>Validates catalog-wide structural rules and returns one message per violation.</summary>
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<ParityScenario> scenarios,
        IReadOnlyCollection<string> canonicalNoProfileIds
    )
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(canonicalNoProfileIds);

        var canonical = new HashSet<string>(canonicalNoProfileIds, StringComparer.Ordinal);
        var violations = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var byId = new Dictionary<string, ParityScenario>(StringComparer.Ordinal);

        foreach (ParityScenario scenario in scenarios)
        {
            if (string.IsNullOrWhiteSpace(scenario.Id))
            {
                violations.Add("A scenario has a blank Id.");
                continue;
            }

            if (!seenIds.Add(scenario.Id))
            {
                violations.Add($"Duplicate scenario id: {scenario.Id}.");
            }

            byId[scenario.Id] = scenario;
        }

        foreach (ParityScenario scenario in scenarios)
        {
            if (string.IsNullOrWhiteSpace(scenario.Id))
            {
                continue;
            }

            string id = scenario.Id;

            if (string.IsNullOrWhiteSpace(scenario.BehavioralContract))
            {
                violations.Add($"{id}: BehavioralContract is required.");
            }

            ValidateLocations(scenario, id, violations);
            ValidateBoundaryForLayer(scenario, id, violations);
            ValidateEngineOwner(id, "PostgreSQL", scenario.PgsqlCoverage, scenario.PgsqlGapOwner, violations);
            ValidateEngineOwner(id, "SQL Server", scenario.MssqlCoverage, scenario.MssqlGapOwner, violations);
            ValidateClassification(scenario, id, byId, canonical, violations);
            ValidateEffectiveEntryPoint(scenario, id, scenarios, violations);

            if (
                scenario.DialectDifference is { } difference
                && (
                    string.IsNullOrWhiteSpace(difference.Description)
                    || string.IsNullOrWhiteSpace(difference.Rationale)
                )
            )
            {
                violations.Add($"{id}: DialectDifference requires a non-empty Description and Rationale.");
            }
        }

        return violations;
    }

    private static void ValidateLocations(ParityScenario scenario, string id, List<string> violations)
    {
        CheckLocations(scenario.PgsqlLocations, id, "PostgreSQL", isUnitLocation: false, violations);
        CheckLocations(scenario.MssqlLocations, id, "SQL Server", isUnitLocation: false, violations);
        CheckLocations(scenario.UnitLocations, id, "unit", isUnitLocation: true, violations);

        CheckCoverageLocationConsistency(
            id,
            "PostgreSQL",
            scenario.PgsqlCoverage,
            scenario.PgsqlLocations,
            violations
        );
        CheckCoverageLocationConsistency(
            id,
            "SQL Server",
            scenario.MssqlCoverage,
            scenario.MssqlLocations,
            violations
        );
    }

    private static void CheckCoverageLocationConsistency(
        string id,
        string engine,
        EngineCoverage coverage,
        ImmutableArray<ScenarioLocation> locations,
        List<string> violations
    )
    {
        if (coverage == EngineCoverage.Covered && locations.IsDefaultOrEmpty)
        {
            violations.Add($"{id}: {engine} coverage is Covered but no {engine} location is recorded.");
        }
        else if (coverage != EngineCoverage.Covered && !locations.IsDefaultOrEmpty)
        {
            violations.Add(
                $"{id}: {engine} coverage is {coverage} but a {engine} location is recorded (only Covered may name a location)."
            );
        }
    }

    private static void CheckLocations(
        ImmutableArray<ScenarioLocation> locations,
        string id,
        string engine,
        bool isUnitLocation,
        List<string> violations
    )
    {
        if (locations.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (ScenarioLocation location in locations)
        {
            if (string.IsNullOrWhiteSpace(location.File) || string.IsNullOrWhiteSpace(location.Fixture))
            {
                violations.Add($"{id}: a {engine} location requires a non-blank File and Fixture.");
            }

            if (location.Methods.IsDefaultOrEmpty || location.Methods.Any(string.IsNullOrWhiteSpace))
            {
                violations.Add($"{id}: a {engine} location requires at least one non-blank test method.");
            }

            // Unit-location ownership: a unit location must name its owning test assembly so a unit-resolution pass
            // validates it only against that assembly, and a per-engine provider location must never carry unit
            // ownership (it is resolved per engine, not per unit assembly).
            if (isUnitLocation && location.UnitOwner is null)
            {
                violations.Add(
                    $"{id}: a unit location must declare an owning test assembly (UnitOwner) so it resolves only against that assembly."
                );
            }
            else if (!isUnitLocation && location.UnitOwner is not null)
            {
                violations.Add(
                    $"{id}: a {engine} provider location must not declare a unit owning assembly (UnitOwner is only valid on a unit location)."
                );
            }
        }
    }

    private static void ValidateEffectiveEntryPoint(
        ParityScenario scenario,
        string id,
        IReadOnlyList<ParityScenario> scenarios,
        List<string> violations
    )
    {
        EffectiveEntryPoint? effective = ParityEntryPointResolution.ResolveEffectiveEntryPoint(
            scenario,
            scenarios
        );
        bool hasRationale = !string.IsNullOrWhiteSpace(scenario.ProviderSpecificEntryPointRationale);

        if (effective is not null)
        {
            // Keep the mode unambiguous: a provider-specific rationale is only meaningful when the row actually
            // resolves provider-specific (it has no direct or inherited shared contract).
            if (effective.Kind != EntryPointKind.ProviderSpecific && hasRationale)
            {
                violations.Add(
                    $"{id}: ProviderSpecificEntryPointRationale is only valid when the effective entry point is "
                        + $"ProviderSpecific (this row resolves as {effective.Kind})."
                );
            }

            // A Direct shared contract must name at least one concrete member (Type.Method), not a bare type.
            // Naming the reusable assertion/helper members is what lets the reflection resolver prove they still
            // exist, so an unused shared assertion cannot be deleted (or fall out of use) with the catalog staying
            // green. Inherited rows reuse a Direct row's value and are validated at that Direct source.
            if (effective.Kind == EntryPointKind.Direct && !string.IsNullOrWhiteSpace(effective.SharedValue))
            {
                foreach (string part in SplitSharedEntryPointParts(effective.SharedValue))
                {
                    if (!part.Contains('.', StringComparison.Ordinal))
                    {
                        violations.Add(
                            $"{id}: the Direct shared entry point part '{part}' must name a member (Type.Method), "
                                + "not a bare type, so the reusable assertion/helper is verified."
                        );
                    }
                }
            }

            return;
        }

        bool hasLocation =
            !scenario.PgsqlLocations.IsDefaultOrEmpty
            || !scenario.MssqlLocations.IsDefaultOrEmpty
            || !scenario.UnitLocations.IsDefaultOrEmpty;

        if (hasLocation && !hasRationale)
        {
            violations.Add(
                $"{id}: a provider-specific effective entry point (no direct or inherited shared contract) requires a "
                    + "ProviderSpecificEntryPointRationale."
            );
        }
        else
        {
            violations.Add(
                $"{id}: no effective reusable assertion/helper entry point resolves — record a direct SharedEntryPoint, "
                    + "an inherited family/covered-by contract, or provider-specific locations with a ProviderSpecificEntryPointRationale."
            );
        }
    }

    // The mechanic (ProductionBoundary) each layer may declare. Each mechanic belongs to exactly one layer, so
    // a no-profile row can never claim a profile mechanic (or vice versa) and "same mechanic" deferral stays
    // meaningful across the whole catalog.
    private static void ValidateBoundaryForLayer(ParityScenario scenario, string id, List<string> violations)
    {
        bool valid = scenario.Layer switch
        {
            ParityLayer.Api => scenario.Boundary == ProductionBoundary.HttpPipeline,
            ParityLayer.Profile => scenario.Boundary
                is ProductionBoundary.ProfilePersistExecutor
                    or ProductionBoundary.ProfileMergeSynthesizer
                    or ProductionBoundary.ProfileCreatabilityAnalysis,
            ParityLayer.NoProfile => scenario.Boundary
                is ProductionBoundary.NoProfilePersister
                    or ProductionBoundary.NoProfileMerge
                    or ProductionBoundary.GuardedNoOp
                    or ProductionBoundary.IdentityStability
                    or ProductionBoundary.BatchSqlEmitter
                    or ProductionBoundary.ReferenceIdentityRuntime
                    or ProductionBoundary.KeyUnificationValidation
                    or ProductionBoundary.RelationalReadback,
            _ => false,
        };

        if (!valid)
        {
            violations.Add(
                $"{id}: mechanic {scenario.Boundary} is not valid for the {scenario.Layer} layer."
            );
        }
    }

    // Splits a shared-entry-point value into its composite parts, matching the reflection resolver's split so a
    // structural "names a member" rule and the reflection "the member exists" rule agree on part boundaries.
    private static IEnumerable<string> SplitSharedEntryPointParts(string sharedValue) =>
        sharedValue.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void ValidateEngineOwner(
        string id,
        string engine,
        EngineCoverage coverage,
        string? owner,
        List<string> violations
    )
    {
        if (coverage == EngineCoverage.Gap && string.IsNullOrWhiteSpace(owner))
        {
            violations.Add($"{id}: the {engine} gap requires an owning ticket.");
        }
        else if (coverage != EngineCoverage.Gap && !string.IsNullOrEmpty(owner))
        {
            violations.Add($"{id}: a {engine} owner is only valid when {engine} coverage is a gap.");
        }
    }

    private static void ValidateClassification(
        ParityScenario scenario,
        string id,
        Dictionary<string, ParityScenario> byId,
        HashSet<string> canonicalNoProfileIds,
        List<string> violations
    )
    {
        switch (scenario.Classification)
        {
            case ParityClassification.KnownGap:
                // A KnownGap row records a case that IS proven on PostgreSQL but whose SQL Server twin is missing.
                // Both halves of that meaning must hold: PostgreSQL must be Covered (or the row has silently lost
                // its only proof) and SQL Server must be a Gap.
                if (scenario.PgsqlCoverage != EngineCoverage.Covered)
                {
                    violations.Add(
                        $"{id}: a KnownGap row must have PgsqlCoverage=Covered because the classification means the case is proven on PostgreSQL while its SQL Server twin is missing."
                    );
                }

                if (scenario.MssqlCoverage != EngineCoverage.Gap)
                {
                    violations.Add(
                        $"{id}: a KnownGap row must have MssqlCoverage=Gap because its SQL Server twin is missing."
                    );
                }

                break;

            case ParityClassification.Both
                when scenario.PgsqlCoverage != EngineCoverage.Covered
                    || scenario.MssqlCoverage != EngineCoverage.Covered:
                violations.Add($"{id}: a Both row requires Covered coverage on both engines.");
                break;

            case ParityClassification.Na:
                ValidateNotApplicable(scenario, id, violations);
                break;

            case ParityClassification.SupportingSmoke:
                ValidateSupportingSmoke(scenario, id, byId, canonicalNoProfileIds, violations);
                break;

            default:
                break;
        }

        if (
            scenario.Classification != ParityClassification.SupportingSmoke
            && !string.IsNullOrEmpty(scenario.CoveredByScenarioId)
        )
        {
            violations.Add($"{id}: CoveredByScenarioId is only valid on a SupportingSmoke row.");
        }

        if (scenario.Classification != ParityClassification.Na && !scenario.UnitLocations.IsDefaultOrEmpty)
        {
            violations.Add($"{id}: Unit locations are only valid on an Na row.");
        }

        if (
            (
                scenario.PgsqlCoverage == EngineCoverage.Mapped
                || scenario.MssqlCoverage == EngineCoverage.Mapped
            )
            && scenario.Classification != ParityClassification.SupportingSmoke
        )
        {
            violations.Add(
                $"{id}: EngineCoverage.Mapped is only valid on the deferred engine of a SupportingSmoke row."
            );
        }
    }

    private static void ValidateNotApplicable(ParityScenario scenario, string id, List<string> violations)
    {
        if (
            scenario.PgsqlCoverage != EngineCoverage.NotApplicable
            || scenario.MssqlCoverage != EngineCoverage.NotApplicable
        )
        {
            violations.Add($"{id}: an Na row must be NotApplicable on both engines.");
        }

        if (scenario.UnitLocations.IsDefaultOrEmpty)
        {
            violations.Add(
                $"{id}: an Na row requires at least one Unit location recording its unit-test entry point."
            );
        }
    }

    private static void ValidateSupportingSmoke(
        ParityScenario scenario,
        string id,
        Dictionary<string, ParityScenario> byId,
        HashSet<string> canonicalNoProfileIds,
        List<string> violations
    )
    {
        if (scenario.Layer != ParityLayer.NoProfile)
        {
            violations.Add($"{id}: a SupportingSmoke row must be in the NoProfile layer.");
        }

        bool oneCoveredOneMapped =
            (
                scenario.PgsqlCoverage == EngineCoverage.Covered
                && scenario.MssqlCoverage == EngineCoverage.Mapped
            )
            || (
                scenario.MssqlCoverage == EngineCoverage.Covered
                && scenario.PgsqlCoverage == EngineCoverage.Mapped
            );
        if (!oneCoveredOneMapped)
        {
            violations.Add(
                $"{id}: a SupportingSmoke row must be Covered on one engine and Mapped on the other."
            );
        }

        if (string.IsNullOrWhiteSpace(scenario.CoveredByScenarioId))
        {
            violations.Add($"{id}: a SupportingSmoke row requires a CoveredByScenarioId.");
            return;
        }

        if (!canonicalNoProfileIds.Contains(scenario.CoveredByScenarioId))
        {
            violations.Add(
                $"{id}: CoveredByScenarioId '{scenario.CoveredByScenarioId}' must equal an exact canonical no-profile id."
            );
        }

        if (!byId.TryGetValue(scenario.CoveredByScenarioId, out ParityScenario? target))
        {
            violations.Add(
                $"{id}: CoveredByScenarioId '{scenario.CoveredByScenarioId}' does not match any scenario."
            );
        }
        else if (target.Layer != ParityLayer.NoProfile || target.Boundary != scenario.Boundary)
        {
            violations.Add(
                $"{id}: CoveredByScenarioId '{scenario.CoveredByScenarioId}' must be a NoProfile scenario at the same production boundary ({scenario.Boundary})."
            );
        }
    }
}
