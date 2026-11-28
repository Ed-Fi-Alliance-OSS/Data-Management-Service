// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

/// <summary>
/// Live proof of one benchmark side on the smoke workload, with the write scenario injected
/// at the rehearsal factor: the side completes, the write verification fails before the
/// writes and passes after, the required values are what the profile pins, pooled
/// connections are reused across the write phase, and the injection touched exactly the
/// write requests.
/// </summary>
internal static class BenchmarkSideSmoke
{
    /// <summary>
    /// New logins or sessions tolerated across the whole write phase. Serial requests over a
    /// reused pool need about one; a connection per request would need one per PUT.
    /// </summary>
    private const long MaximumNewConnectionsDuringWrites = 5;

    public static PerfBenchmarkSidePlan Plan(PerfProvider provider)
    {
        PerfLoadedBenchmarkProfile profile = PerfBenchmarkProfileLoader.LoadFile(
            PerfBenchmarkProfileLoader.DefaultPath
        );
        decimal writeFactor =
            profile.Profile.SmokeWorkload.InjectionFactors[PerfBenchmarkScenarios.StudentPutCollectionUpdate]
            ?? throw new InvalidOperationException("The smoke workload has no rehearsal write factor.");

        return new PerfBenchmarkSidePlan(
            provider,
            profile,
            Smoke: true,
            new PerfBenchmarkSideIdentity(
                $"smoke-{Guid.NewGuid():N}",
                PerfBenchmarkSideResult.CandidateRole,
                1,
                2
            ),
            PerfSlowdownSpec.Parse(
                $"{PerfBenchmarkScenarios.StudentPutCollectionUpdate}={writeFactor.ToString(CultureInfo.InvariantCulture)}"
            ),
            Path.Combine(Path.GetTempPath(), "dms-perf-benchmark-side-smoke", Guid.NewGuid().ToString("N")),
            GitIdentity.HeadCommit(AppContext.BaseDirectory)
        );
    }

    public static async Task RunAsync(
        ApiIntegrationHarness harness,
        PerfBenchmarkSidePlan plan,
        PerfSlowdownInjection injection,
        string leasedConnectionString,
        Func<Task<DbConnection>> openReplayConnectionAsync,
        string imageTag,
        string imageDigest
    )
    {
        long connectionsBeforeWrites = -1;
        long connectionsAfterWrites = -1;

        PerfBenchmarkSideOutcome outcome = await PerfBenchmarkSidePipeline.RunAsync(
            plan,
            PerfBenchmarkSidePipeline.LiveSteps(
                harness,
                plan,
                leasedConnectionString,
                openReplayConnectionAsync,
                new PerfEvidenceRunSettings(
                    imageTag,
                    imageDigest,
                    "local docker volume, not tmpfs",
                    AllowCi: true,
                    AllowedDirtyPrefixes: [PerfEvidenceRunSettings.DefaultAllowedDirtyPrefix],
                    AllowAnyDirtyPath: true
                ),
                injection,
                beforeWrites: async () =>
                    connectionsBeforeWrites = await ConnectionCountAsync(harness.DbConnection, plan.Provider),
                afterWrites: async () =>
                    connectionsAfterWrites = await ConnectionCountAsync(harness.DbConnection, plan.Provider)
            )
        );

        PerfBenchmarkSideWorkload workload = plan.Workload;
        PerfBenchmarkSideResult side = PerfBenchmarkSideResults.Read(
            await File.ReadAllTextAsync(Path.Combine(plan.SideDirectory, PerfBenchmarkSideResult.FileName))
        );
        side.Status.State.Should().Be(PerfBenchmarkSideStatus.Completed);
        side.RunId.Should().Be(plan.Identity.RunId);

        // Non-vacuous write verification: it must fail on the untouched fixture.
        outcome
            .Writes.MismatchesBeforeWrites.Should()
            .Contain(
                $"updated-other-names-on-targets: expected {plan.Workload.Writes.WarmupIterations + plan.Workload.Writes.MeasuredIterations}, got 0."
            );
        outcome.Writes.MismatchesAfterWrites.Should().BeEmpty();

        // Every PUT answered 204 with an etag, and the row totals are unchanged.
        side.Write.WarmupCompleted.Should().Be(workload.Writes.WarmupIterations);
        side.Write.MeasuredCompleted.Should().Be(workload.Writes.MeasuredIterations);
        side.Write.Failed.Should().Be(0);
        (await ScalarAsync(harness.DbConnection, StudentCountSql(plan.Provider)))
            .Should()
            .Be(plan.FixtureDefinition.RowCount);

        // Valid 1.3.0 read artifacts (the paging pipeline re-validates them) and read counts
        // equal to the plan.
        (await File.ReadAllLinesAsync(Path.Combine(outcome.ReadRunDirectory, "results.csv")))
            .Should()
            .HaveCount(7);
        side.ReadCells.Should()
            .AllSatisfy(cell =>
            {
                cell.WarmupCompleted.Should().Be(workload.Reads.WarmupIterations);
                cell.MeasuredCompleted.Should().Be(workload.Reads.MeasuredIterations);
                cell.MeasuredWindow.Should().NotBeNull();
            });

        // The required values the comparison asserts directly.
        side.EffectiveLogLevel.Should().Be("Warning");
        side.OwnershipPublished.Should().BeTrue();
        side.EffectiveConnection.Should()
            .Be(plan.Profile.Profile.Workload.Connection[PerfProviders.ArtifactName(plan.Provider)]);

        // Connection reuse across the write phase.
        long newConnections = connectionsAfterWrites - connectionsBeforeWrites;
        await TestContext.Out.WriteLineAsync(
            $"New connections across {workload.Writes.WarmupIterations + workload.Writes.MeasuredIterations} writes: {newConnections}"
        );
        connectionsBeforeWrites.Should().BeGreaterThanOrEqualTo(0);
        newConnections.Should().BeInRange(0, MaximumNewConnectionsDuringWrites);

        // The injection touched exactly the write requests.
        side.Injection.AffectedRequests.Should()
            .BeEquivalentTo(
                PerfBenchmarkScenarios.AllIds.ToDictionary(
                    id => id,
                    id =>
                        id == PerfBenchmarkScenarios.StudentPutCollectionUpdate
                            ? workload.Writes.WarmupIterations + workload.Writes.MeasuredIterations
                            : 0
                )
            );

        await TestContext.Out.WriteLineAsync($"Validated benchmark side written to {plan.SideDirectory}");
    }

    /// <summary>
    /// PostgreSQL: sessions established to this database (a backend that exits flushes its
    /// count, so a connection per request shows up in full). SQL Server: the cumulative
    /// Logins/sec counter; a pooled connection's reuse is not a login.
    /// </summary>
    private static Task<long> ConnectionCountAsync(DbConnection connection, PerfProvider provider) =>
        ScalarAsync(
            connection,
            provider == PerfProvider.Postgresql
                ? "SELECT sessions FROM pg_stat_database WHERE datname = current_database();"
                : "SELECT cntr_value FROM sys.dm_os_performance_counters "
                    + "WHERE counter_name = 'Logins/sec' AND object_name LIKE '%General Statistics%';"
        );

    private static string StudentCountSql(PerfProvider provider) =>
        provider == PerfProvider.Postgresql
            ? """SELECT COUNT(*) FROM "edfi"."Student";"""
            : "SELECT COUNT(*) FROM [edfi].[Student];";

    private static async Task<long> ScalarAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
