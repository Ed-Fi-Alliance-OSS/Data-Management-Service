// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// One measured final-gate cell, deliberately flat so it mirrors one CSV row. Nullable fields
/// follow the family: traditional rows carry a page size and offset; cursor rows carry a page
/// size, range, and start anchor; partition rows carry a requested number and returned token
/// count and nothing page-shaped. The timing layers keep the baseline semantics: app-level
/// wall clock, driver-observed execute interval (diagnostic only), and full replay metrics.
/// </summary>
public sealed record PerfFinalGateScenarioResult(
    string Provider,
    string ScenarioId,
    string Family,
    string Variant,
    string? Phase,
    int? PageSize,
    long? Offset,
    string? CursorRange,
    long? StartAnchorDocumentId,
    int? RequestedPartitionNumber,
    int? ReturnedRows,
    int? ReturnedTokenCount,
    int CommandCountPerRequest,
    int WarmupIterations,
    int MeasuredIterations,
    PerfLatencySummary LatencyMs,
    PerfLatencySummary DriverExecuteMs,
    PerfDatabaseMetrics Database,
    string PlanFile,
    string SelectionSqlSha256,
    string ReplayParameterSource,
    string RunnerCommit,
    string SubjectCommit
)
{
    /// <summary>
    /// The per-cell artifact file key: the page size for traditional/cursor rows, the
    /// requested count for partition rows. Plan and sql file names embed it.
    /// </summary>
    public string CellKey =>
        PageSize?.ToString(CultureInfo.InvariantCulture)
        ?? RequestedPartitionNumber?.ToString(CultureInfo.InvariantCulture)
        ?? string.Empty;
}

/// <summary>
/// The versioned root of a final-gate results.json. Row order is execution order; the
/// validator holds it to the catalog's exact cell sequence for the manifest's run kind.
/// </summary>
public sealed record PerfFinalGateResultsDocument(
    string SchemaVersion,
    IReadOnlyList<PerfFinalGateScenarioResult> Results
)
{
    public static PerfFinalGateResultsDocument Create(IEnumerable<PerfFinalGateScenarioResult> results) =>
        new(PerfFinalGateArtifactSchema.Version, [.. results]);
}
