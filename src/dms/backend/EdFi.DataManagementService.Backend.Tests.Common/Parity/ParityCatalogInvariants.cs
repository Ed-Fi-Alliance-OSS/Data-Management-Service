// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Tests.Common.Parity;

/// <summary>
/// Pure structural invariants over a parity catalog. Returns a human-readable message per
/// violation (empty when the catalog is well-formed). No reflection or database access, so it
/// runs in the unit lane and is testable with synthetic catalogs.
/// </summary>
public static class ParityCatalogInvariants
{
    /// <summary>Validates catalog-wide structural rules and returns one message per violation.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<ParityScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);

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

            ValidateGapOwnership(scenario, id, violations);
            ValidateSupportingSmoke(scenario, id, byId, violations);
            ValidateEngineCoverage(scenario, id, violations);
            ValidateNotApplicable(scenario, id, violations);

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

            foreach (string supportingId in scenario.SupportingScenarioIds)
            {
                if (!byId.ContainsKey(supportingId))
                {
                    violations.Add(
                        $"{id}: SupportingScenarioIds references unknown scenario '{supportingId}'."
                    );
                }
            }
        }

        return violations;
    }

    private static void ValidateGapOwnership(ParityScenario scenario, string id, List<string> violations)
    {
        if (scenario.Classification == ParityClassification.KnownGap)
        {
            if (string.IsNullOrWhiteSpace(scenario.GapOwner))
            {
                violations.Add($"{id}: a KnownGap row requires a GapOwner.");
            }

            if (scenario.MssqlCoverage != EngineCoverage.Gap)
            {
                violations.Add(
                    $"{id}: a KnownGap row must have MssqlCoverage=Gap because its SQL Server twin is missing."
                );
            }
        }
        else if (!string.IsNullOrEmpty(scenario.GapOwner))
        {
            violations.Add($"{id}: GapOwner is only valid on a KnownGap row.");
        }
    }

    private static void ValidateSupportingSmoke(
        ParityScenario scenario,
        string id,
        Dictionary<string, ParityScenario> byId,
        List<string> violations
    )
    {
        if (scenario.Classification == ParityClassification.SupportingSmoke)
        {
            if (string.IsNullOrWhiteSpace(scenario.CoveredByScenarioId))
            {
                violations.Add($"{id}: a SupportingSmoke row requires a CoveredByScenarioId.");
            }
            else if (!byId.TryGetValue(scenario.CoveredByScenarioId, out ParityScenario? target))
            {
                violations.Add(
                    $"{id}: CoveredByScenarioId '{scenario.CoveredByScenarioId}' does not match any scenario."
                );
            }
            else if (target.Layer != ParityLayer.NoProfile)
            {
                violations.Add(
                    $"{id}: CoveredByScenarioId '{scenario.CoveredByScenarioId}' must be a NoProfile-layer scenario at the same production boundary."
                );
            }
        }
        else if (!string.IsNullOrEmpty(scenario.CoveredByScenarioId))
        {
            violations.Add($"{id}: CoveredByScenarioId is only valid on a SupportingSmoke row.");
        }
    }

    private static void ValidateEngineCoverage(ParityScenario scenario, string id, List<string> violations)
    {
        if (
            scenario.Classification == ParityClassification.Both
            && (
                scenario.Pgsql is null
                || scenario.Mssql is null
                || scenario.PgsqlCoverage != EngineCoverage.Covered
                || scenario.MssqlCoverage != EngineCoverage.Covered
            )
        )
        {
            violations.Add(
                $"{id}: a Both row requires PostgreSQL and SQL Server locations with Covered coverage on each engine."
            );
        }

        if (scenario.PgsqlCoverage == EngineCoverage.Covered && scenario.Pgsql is null)
        {
            violations.Add($"{id}: PostgreSQL coverage is Covered but no PostgreSQL location is recorded.");
        }

        if (scenario.MssqlCoverage == EngineCoverage.Covered && scenario.Mssql is null)
        {
            violations.Add($"{id}: SQL Server coverage is Covered but no SQL Server location is recorded.");
        }
    }

    private static void ValidateNotApplicable(ParityScenario scenario, string id, List<string> violations)
    {
        if (scenario.Classification != ParityClassification.Na)
        {
            return;
        }

        if (
            scenario.PgsqlCoverage != EngineCoverage.NotApplicable
            || scenario.MssqlCoverage != EngineCoverage.NotApplicable
        )
        {
            violations.Add($"{id}: an Na row must be NotApplicable on both engines.");
        }

        if (string.IsNullOrWhiteSpace(scenario.Notes))
        {
            violations.Add($"{id}: an Na row requires Notes recording its unit-level entry point.");
        }
    }
}
