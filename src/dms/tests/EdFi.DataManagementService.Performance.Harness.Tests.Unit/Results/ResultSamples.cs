// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

internal static class ResultSamples
{
    public const string RunnerCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string SubjectCommit = "cccccccccccccccccccccccccccccccccccccccc";
    public static readonly string Sha256 = new('a', 64);

    public static PerfScenarioResult Postgresql(
        string scenarioId = PerfScenarios.TraditionalOffsetZero,
        int pageSize = 25
    ) =>
        new(
            Provider: "postgresql",
            ScenarioId: scenarioId,
            PageSize: pageSize,
            Offset: 0,
            ReturnedRows: pageSize,
            CommandCountPerRequest: 1,
            WarmupIterations: 5,
            MeasuredIterations: 30,
            LatencyMs: new(12.5, 20.25, 14.125, 10.0, 22.5, [12.5, 20.25, 10.0]),
            DbCommandMs: new(8.5, 15.0, 10.0, 7.5, 16.0, [8.5, 15.0, 7.5]),
            Database: new(
                BuffersHit: 1200,
                BuffersRead: 34,
                DbExecutionMs: 6.25,
                LogicalReads: null,
                PhysicalReads: null,
                DbCpuMs: null,
                DbElapsedMs: null
            ),
            PlanFile: "plans/pg.traditional-offset-zero.25.explain.json",
            PageSelectionSqlSha256: Sha256,
            RunnerCommit: RunnerCommit,
            SubjectCommit: SubjectCommit
        );

    public static PerfScenarioResult Mssql(
        string scenarioId = PerfScenarios.TraditionalOffsetZero,
        int pageSize = 25
    ) =>
        new(
            Provider: "mssql",
            ScenarioId: scenarioId,
            PageSize: pageSize,
            Offset: 0,
            ReturnedRows: pageSize,
            CommandCountPerRequest: 1,
            WarmupIterations: 5,
            MeasuredIterations: 30,
            LatencyMs: new(13.5, 21.25, 15.125, 11.0, 23.5, [13.5, 21.25, 11.0]),
            DbCommandMs: new(9.5, 16.0, 11.0, 8.5, 17.0, [9.5, 16.0, 8.5]),
            Database: new(
                BuffersHit: null,
                BuffersRead: null,
                DbExecutionMs: null,
                LogicalReads: 2100,
                PhysicalReads: 12,
                DbCpuMs: 5.0,
                DbElapsedMs: 7.75
            ),
            PlanFile: "plans/mssql.traditional-offset-zero.25.sqlplan",
            PageSelectionSqlSha256: Sha256,
            RunnerCommit: RunnerCommit,
            SubjectCommit: SubjectCommit
        );

    public static PerfRunManifest Manifest() =>
        PerfRunManifest.Create(
            new PerfRunIdentity("pg-primary-500k-20260820", "2026-08-20T12:00:00Z", "postgresql"),
            new PerfCommitIdentity(
                RunnerCommit,
                SubjectCommit,
                ["src/dms/tests/EdFi.DataManagementService.Performance.Harness/"]
            ),
            new PerfManifestFixture("primary-500k", 500_000, 450_000),
            new PerfIterationPlan(5, 30, [.. PerfScenarios.AllIds]),
            PerfEnvironmentIdentity.Create(
                PerfServerIdentity.Create(
                    "PostgreSQL 16.8",
                    "postgres:16.8-alpine",
                    "sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0",
                    "local volume, not tmpfs",
                    [new PerfSetting("work_mem", "4MB"), new PerfSetting("shared_buffers", "128MB")]
                ),
                new PerfHostIdentity("Windows 11", "X64", 16, "10.0.400", false, "f0e1d2c3b4a59687"),
                [new PerfSetting("Npgsql", "8.0.4")]
            )
        );
}
