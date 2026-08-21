// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// The versioned root of results.json. Construct through <see cref="Create" /> so the schema
/// version is stamped and the results carry the canonical ordering.
/// </summary>
public sealed record PerfResultsDocument(string SchemaVersion, IReadOnlyList<PerfScenarioResult> Results)
{
    public static PerfResultsDocument Create(IEnumerable<PerfScenarioResult> results) =>
        new(PerfArtifactSchema.Version, PerfResultOrdering.Order(results));
}
