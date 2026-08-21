// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Canonical artifact ordering: provider, then the scenario matrix's own order (zero, shallow,
/// deep), then page size. Applied by every writer so artifact diffs are stable regardless of
/// the order cells were measured in.
/// </summary>
public static class PerfResultOrdering
{
    public static IReadOnlyList<PerfScenarioResult> Order(IEnumerable<PerfScenarioResult> results) =>
        [
            .. results
                .OrderBy(result => result.Provider, StringComparer.Ordinal)
                .ThenBy(result => ScenarioRank(result.ScenarioId))
                .ThenBy(result => result.ScenarioId, StringComparer.Ordinal)
                .ThenBy(result => result.PageSize),
        ];

    private static int ScenarioRank(string scenarioId)
    {
        for (int index = 0; index < PerfScenarios.AllIds.Count; index++)
        {
            if (PerfScenarios.AllIds[index] == scenarioId)
            {
                return index;
            }
        }

        // Unknown scenario ids sort after the known matrix; rejecting them is the result
        // validator's job, not the writer's.
        return PerfScenarios.AllIds.Count;
    }
}
