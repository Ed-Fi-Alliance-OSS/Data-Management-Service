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
            ValidateEngineOwner(id, "PostgreSQL", scenario.PgsqlCoverage, scenario.PgsqlGapOwner, violations);
            ValidateEngineOwner(id, "SQL Server", scenario.MssqlCoverage, scenario.MssqlGapOwner, violations);
            ValidateClassification(scenario, id, byId, canonical, violations);

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
        CheckLocations(scenario.PgsqlLocations, id, "PostgreSQL", violations);
        CheckLocations(scenario.MssqlLocations, id, "SQL Server", violations);
        CheckLocations(scenario.UnitLocations, id, "unit", violations);

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
        }
    }

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
            case ParityClassification.KnownGap when scenario.MssqlCoverage != EngineCoverage.Gap:
                violations.Add(
                    $"{id}: a KnownGap row must have MssqlCoverage=Gap because its SQL Server twin is missing."
                );
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
