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

            ValidateLocations(scenario, id, violations);
            ValidateGapOwnership(scenario, id, violations);
            ValidateSupportingSmoke(scenario, id, byId, violations);
            ValidateNotApplicable(scenario, id, violations);

            if (
                scenario.Classification == ParityClassification.Both
                && (
                    scenario.PgsqlCoverage != EngineCoverage.Covered
                    || scenario.MssqlCoverage != EngineCoverage.Covered
                )
            )
            {
                violations.Add($"{id}: a Both row requires Covered coverage on both engines.");
            }

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
        CheckLocation(scenario.Pgsql, id, "PostgreSQL", violations);
        CheckLocation(scenario.Mssql, id, "SQL Server", violations);
        CheckLocation(scenario.Unit, id, "unit", violations);

        if (scenario.PgsqlCoverage == EngineCoverage.Covered && scenario.Pgsql is null)
        {
            violations.Add($"{id}: PostgreSQL coverage is Covered but no PostgreSQL location is recorded.");
        }

        if (scenario.MssqlCoverage == EngineCoverage.Covered && scenario.Mssql is null)
        {
            violations.Add($"{id}: SQL Server coverage is Covered but no SQL Server location is recorded.");
        }
    }

    private static void CheckLocation(
        ScenarioLocation? location,
        string id,
        string engine,
        List<string> violations
    )
    {
        if (location is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(location.File) || string.IsNullOrWhiteSpace(location.Fixture))
        {
            violations.Add($"{id}: the {engine} location requires a non-blank File and Fixture.");
        }

        if (location.Methods.IsDefaultOrEmpty || location.Methods.Any(string.IsNullOrWhiteSpace))
        {
            violations.Add($"{id}: the {engine} location requires at least one non-blank test method.");
        }
    }

    private static void ValidateGapOwnership(ParityScenario scenario, string id, List<string> violations)
    {
        if (scenario.Classification == ParityClassification.KnownGap)
        {
            if (scenario.MssqlCoverage != EngineCoverage.Gap)
            {
                violations.Add(
                    $"{id}: a KnownGap row must have MssqlCoverage=Gap because its SQL Server twin is missing."
                );
            }

            CheckEngineOwner(id, "PostgreSQL", scenario.PgsqlCoverage, scenario.PgsqlGapOwner, violations);
            CheckEngineOwner(id, "SQL Server", scenario.MssqlCoverage, scenario.MssqlGapOwner, violations);
        }
        else if (
            !string.IsNullOrEmpty(scenario.PgsqlGapOwner) || !string.IsNullOrEmpty(scenario.MssqlGapOwner)
        )
        {
            violations.Add($"{id}: gap owners are only valid on a KnownGap row.");
        }
    }

    private static void CheckEngineOwner(
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
            else
            {
                if (target.Layer != ParityLayer.NoProfile || target.Boundary != scenario.Boundary)
                {
                    violations.Add(
                        $"{id}: CoveredByScenarioId '{scenario.CoveredByScenarioId}' must be a NoProfile scenario at the same production boundary ({scenario.Boundary})."
                    );
                }

                if (target.Classification is ParityClassification.SupportingSmoke or ParityClassification.Na)
                {
                    violations.Add(
                        $"{id}: CoveredByScenarioId '{scenario.CoveredByScenarioId}' must be a canonical scenario, not a supporting-smoke or Na row."
                    );
                }
            }
        }
        else if (!string.IsNullOrEmpty(scenario.CoveredByScenarioId))
        {
            violations.Add($"{id}: CoveredByScenarioId is only valid on a SupportingSmoke row.");
        }
    }

    private static void ValidateNotApplicable(ParityScenario scenario, string id, List<string> violations)
    {
        if (scenario.Classification == ParityClassification.Na)
        {
            if (
                scenario.PgsqlCoverage != EngineCoverage.NotApplicable
                || scenario.MssqlCoverage != EngineCoverage.NotApplicable
            )
            {
                violations.Add($"{id}: an Na row must be NotApplicable on both engines.");
            }

            if (scenario.Unit is null)
            {
                violations.Add(
                    $"{id}: an Na row requires a Unit location recording its unit-test entry point."
                );
            }
        }
        else if (scenario.Unit is not null)
        {
            violations.Add($"{id}: a Unit location is only valid on an Na row.");
        }
    }
}
