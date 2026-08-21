// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// One measured scenario cell. Deliberately flat so it mirrors one CSV row. The three timing
/// layers are distinct: <paramref name="LatencyMs" /> is the app-level request wall clock,
/// <paramref name="DriverExecuteMs" /> is the driver-observed execute/dispatch interval from
/// provider diagnostics — diagnostic evidence only, not guaranteed to include reader or
/// result-set consumption (SqlClient's after event fires when ExecuteReader returns) —
/// and <paramref name="Database" /> holds plan-replay metrics for the full hydration batch.
/// </summary>
public sealed record PerfScenarioResult(
    string Provider,
    string ScenarioId,
    int PageSize,
    long Offset,
    int ReturnedRows,
    int CommandCountPerRequest,
    int WarmupIterations,
    int MeasuredIterations,
    PerfLatencySummary LatencyMs,
    PerfLatencySummary DriverExecuteMs,
    PerfDatabaseMetrics Database,
    string PlanFile,
    string PageSelectionSqlSha256,
    string RunnerCommit,
    string SubjectCommit
);
