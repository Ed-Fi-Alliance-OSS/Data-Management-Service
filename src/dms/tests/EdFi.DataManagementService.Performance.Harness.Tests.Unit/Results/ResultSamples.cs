// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

internal static class ResultSamples
{
    public const string RunnerCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string SubjectCommit = "cccccccccccccccccccccccccccccccccccccccc";
    public const long DeepOffset = 450_000;
    public static readonly string Sha256 = new('a', 64);

    private static long OffsetFor(string scenarioId, int pageSize) =>
        scenarioId switch
        {
            PerfScenarios.TraditionalOffsetZero => 0,
            PerfScenarios.TraditionalOffsetShallow => pageSize,
            _ => DeepOffset,
        };

    /// <summary>
    /// A thirty-sample latency summary whose statistics genuinely derive from its samples:
    /// the validator recomputes every summary statistic from the retained samples, so
    /// hand-authored statistics would be rejected.
    /// </summary>
    private static PerfLatencySummary SummaryOf(double baseMs) =>
        PerfLatencyMeasurement.Summarize([.. Enumerable.Range(0, 30).Select(i => baseMs + (i * 0.5))]);

    public static PerfScenarioResult Postgresql(
        string scenarioId = PerfScenarios.TraditionalOffsetZero,
        int pageSize = 25
    ) =>
        new(
            Provider: "postgresql",
            ScenarioId: scenarioId,
            PageSize: pageSize,
            Offset: OffsetFor(scenarioId, pageSize),
            ReturnedRows: pageSize,
            CommandCountPerRequest: 1,
            WarmupIterations: 5,
            MeasuredIterations: 30,
            LatencyMs: SummaryOf(10.0),
            DbCommandMs: SummaryOf(7.5),
            Database: new(
                BuffersHit: 1200,
                BuffersRead: 34,
                DbExecutionMs: 6.25,
                LogicalReads: null,
                PhysicalReads: null,
                DbCpuMs: null,
                DbElapsedMs: null
            ),
            PlanFile: $"plans/pg.{scenarioId}.{pageSize}.explain.json",
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
            Offset: OffsetFor(scenarioId, pageSize),
            ReturnedRows: pageSize,
            CommandCountPerRequest: 1,
            WarmupIterations: 5,
            MeasuredIterations: 30,
            LatencyMs: SummaryOf(11.0),
            DbCommandMs: SummaryOf(8.5),
            Database: new(
                BuffersHit: null,
                BuffersRead: null,
                DbExecutionMs: null,
                LogicalReads: 2100,
                PhysicalReads: 12,
                DbCpuMs: 5.0,
                DbElapsedMs: 7.75
            ),
            PlanFile: $"plans/mssql.{scenarioId}.{pageSize}.sqlplan",
            PageSelectionSqlSha256: Sha256,
            RunnerCommit: RunnerCommit,
            SubjectCommit: SubjectCommit
        );

    public static PerfResultsDocument PostgresqlDocument() =>
        PerfResultsDocument.Create(
            PerfScenarios.AllIds.SelectMany(scenarioId =>
                PerfScenarios.PageSizes.Select(pageSize => Postgresql(scenarioId, pageSize))
            )
        );

    public static PerfResultsDocument MssqlDocument() =>
        PerfResultsDocument.Create(
            PerfScenarios.AllIds.SelectMany(scenarioId =>
                PerfScenarios.PageSizes.Select(pageSize => Mssql(scenarioId, pageSize))
            )
        );

    public static PerfRunManifest Manifest(string provider = "postgresql") =>
        PerfRunManifest.Create(
            new PerfRunIdentity($"{provider}-primary-500k-20260820", "2026-08-20T12:00:00Z", provider),
            new PerfCommitIdentity(
                RunnerCommit,
                SubjectCommit,
                ["src/dms/tests/EdFi.DataManagementService.Performance.Harness/"]
            ),
            new PerfManifestFixture("primary-500k", 500_000, DeepOffset),
            new PerfIterationPlan(
                5,
                30,
                [
                    .. PerfScenarios.AllIds.SelectMany(scenarioId =>
                        PerfScenarios.PageSizes.Select(pageSize => new PerfExecutedCell(
                            scenarioId,
                            pageSize,
                            OffsetFor(scenarioId, pageSize)
                        ))
                    ),
                ]
            ),
            PerfEnvironmentIdentity.Create(
                PerfServerIdentity.Create(
                    "PostgreSQL 16.8",
                    "postgres:16.8-alpine",
                    "sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0",
                    "local volume, not tmpfs",
                    "host=localhost;port=5435;username=postgres;password=REDACTED;"
                        + "database=perf;pooling=true;minimum pool size=10;maximum pool size=50",
                    [new PerfSetting("work_mem", "4MB"), new PerfSetting("shared_buffers", "128MB")]
                ),
                new PerfHostIdentity(
                    "Windows 11",
                    "X64",
                    "AMD Ryzen 9 7950X",
                    16,
                    68_719_476_736,
                    "10.0.400",
                    false,
                    "f0e1d2c3b4a59687"
                ),
                [new PerfSetting("Npgsql", "8.0.4")]
            )
        );
}
