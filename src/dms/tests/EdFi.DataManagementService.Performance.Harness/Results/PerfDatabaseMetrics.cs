// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Database-side metrics captured by replaying the recorded page-selection statement. Each
/// provider fills only its own fields: PostgreSQL reports buffers and execution time from
/// EXPLAIN (ANALYZE, BUFFERS); SQL Server reports reads and CPU/elapsed time from
/// SET STATISTICS IO, TIME. The inapplicable fields stay null and are omitted from JSON.
/// </summary>
public sealed record PerfDatabaseMetrics(
    long? BuffersHit,
    long? BuffersRead,
    double? DbExecutionMs,
    long? LogicalReads,
    long? PhysicalReads,
    double? DbCpuMs,
    double? DbElapsedMs
);
