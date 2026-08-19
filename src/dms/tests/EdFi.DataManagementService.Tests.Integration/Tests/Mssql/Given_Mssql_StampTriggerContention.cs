// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using FluentAssertions.Execution;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// Measures what concurrent writes cost inside the generated <c>_Stamp</c> triggers, on both sides of
/// a change to them. Two cases, each against a freshly leased database:
///
/// <list type="number">
/// <item>concurrent descriptor creates with <c>dms.Descriptor</c> starting empty - the workload the
/// field data implicates, and the one the near-empty-table hypothesis predicts is worst; nothing else
/// in the repository exercises it;</item>
/// <item>concurrent ChartOfAccount creates - the child-collection path, where a child insert re-fires
/// the root stamping trigger.</item>
/// </list>
///
/// <para>Deliberately <b>no</b> <c>deadlockGraphs &gt; 0</c> precondition. This fixture has to produce
/// a comparable report on an unfixed baseline and on a candidate where the cycles are gone, so a
/// precondition that the load still deadlocks would make the candidate side unrunnable. The
/// reproduction that does need that precondition is
/// <see cref="Given_Mssql_ConcurrentCreatesUnderDeadlockPressure"/>, and it is left alone.</para>
///
/// <para>The only assertion is that every accepted create is durable. Throughput and contention
/// thresholds are adjudicated by comparing two runs of this fixture, not inside it: a timing
/// assertion here would be flaky on shared hardware and would not be evidence.</para>
/// </summary>
public sealed class Given_Mssql_StampTriggerContention : MssqlConcurrentWriteLoadTestBase
{
    private const int Workers = 16;
    private const int PerWorker = 25;
    private const int TotalAttempts = Workers * PerWorker;

    /// <summary>
    /// The descriptor resources case 1 spreads its creates across. Every one of them stores into the
    /// single shared <c>dms.Descriptor</c> table and fires the single <c>dms.Descriptor</c> stamping
    /// trigger, so the variety changes the discriminator rather than the contended object - which is
    /// what makes this a measurement of that trigger rather than of eight unrelated ones.
    /// </summary>
    private static readonly string[] DescriptorResources =
    [
        "academicSubjectDescriptors",
        "accountTypeDescriptors",
        "addressTypeDescriptors",
        "assessmentCategoryDescriptors",
        "behaviorDescriptors",
        "calendarEventDescriptors",
        "contactTypeDescriptors",
        "countryDescriptors",
    ];

    [Test]
    [Explicit("Concurrency load measurement; drives deliberate lock contention.")]
    public async Task It_measures_contention_for_concurrent_descriptor_creates_from_near_empty()
    {
        AllowRequestsToOutlastDefaultClientTimeout();

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string ns = $"uri://ed-fi.org/Dms1381/{suffix}";

        await ReportLeasedDatabaseConfigurationAsync();

        // Nothing is seeded here on purpose: an empty dms.Descriptor is the condition under test, and
        // this is asserted rather than assumed because a baseline that ships descriptors would leave
        // the case silently measuring something else.
        long descriptorsBefore = await CountAllDescriptorsAsync();
        await TestContext.Out.WriteLineAsync(
            $"--- dms.Descriptor rows before the load: {descriptorsBefore} ---"
        );
        descriptorsBefore
            .Should()
            .Be(
                0,
                "this case measures descriptor creates against a near-empty dms.Descriptor; a "
                    + "pre-populated table makes the measurement incomparable to the field workload"
            );

        LoadResult result = await DriveConcurrentCreatesAsync(
            (worker, index) =>
                PostAsync(
                    $"/data/ed-fi/{DescriptorResources[worker % DescriptorResources.Length]}",
                    BuildDescriptor(ns, suffix, worker, index)
                )
        );

        long persisted = await CountDescriptorsInNamespaceAsync(ns);
        await ReportCaseAsync("descriptor creates from near-empty", result, persisted);

        persisted
            .Should()
            .Be(
                result.Accepted,
                "every accepted create must be durable; a gap is document loss and blocks landing "
                    + "regardless of what the throughput numbers say"
            );
    }

    [Test]
    [Explicit("Concurrency load measurement; drives deliberate lock contention.")]
    public async Task It_measures_contention_for_concurrent_chart_of_account_creates()
    {
        AllowRequestsToOutlastDefaultClientTimeout();

        string suffix = Guid.NewGuid().ToString("N")[..8];
        long schoolId = 1_000_000_000L + (Convert.ToInt64(suffix, 16) % 1_000_000_000L);
        string ns = $"uri://ed-fi.org/Dms1381/{suffix}";

        await ReportLeasedDatabaseConfigurationAsync();
        await SeedCoreDescriptorsAsync(ns);

        var (status, body) = await PostAsync(
            "/data/ed-fi/schools",
            BuildSchool(ns, schoolId, suffix, documentPath: null)
        );
        status.Should().Be(HttpStatusCode.Created, $"the load needs a school to reference, but got: {body}");

        LoadResult result = await DriveConcurrentCreatesAsync(
            (worker, index) =>
                PostAsync(
                    "/data/ed-fi/chartOfAccounts",
                    BuildChartOfAccount(ns, schoolId, $"{suffix}-{worker:D2}-{index:D3}", worker, index)
                )
        );

        long persisted = await CountPersistedChartOfAccountsAsync(suffix);
        await ReportCaseAsync("chartOfAccount creates", result, persisted);

        persisted
            .Should()
            .Be(
                result.Accepted,
                "every accepted create must be durable; a gap is document loss and blocks landing "
                    + "regardless of what the throughput numbers say"
            );
    }

    /// <summary>What one case measured. Everything here is reported; only the counts are asserted on.</summary>
    private sealed record LoadResult(
        TimeSpan Elapsed,
        int Accepted,
        IReadOnlyCollection<(int Status, string Body)> Failures,
        DeadlockCapture Deadlocks,
        LockWaitTotals LockWaits
    );

    /// <summary>
    /// Drives the load and brackets it with the two readings a differential comparison needs. The
    /// lock-wait reading closes before the deadlock capture runs, because that capture waits out the
    /// Extended Events dispatch latency and idle time inside the window would dilute the wait deltas.
    /// </summary>
    private async Task<LoadResult> DriveConcurrentCreatesAsync(
        Func<int, int, Task<(HttpStatusCode Status, string Body)>> createAsync
    )
    {
        await StartDeadlockCaptureAsync();
        LockWaitTotals lockWaitsBefore = await CaptureLockWaitsAsync();

        ConcurrentBag<(int Status, string Body)> responses = [];
        Stopwatch elapsed = Stopwatch.StartNew();

        await Task.WhenAll(
            Enumerable
                .Range(0, Workers)
                .Select(worker =>
                    Task.Run(async () =>
                    {
                        for (int index = 0; index < PerWorker; index++)
                        {
                            var (status, body) = await createAsync(worker, index);
                            responses.Add(((int)status, body));
                        }
                    })
                )
        );

        elapsed.Stop();

        LockWaitTotals lockWaits = (await CaptureLockWaitsAsync()).Since(lockWaitsBefore);
        DeadlockCapture deadlocks = await CaptureDeadlockSignaturesAsync();

        return new LoadResult(
            elapsed.Elapsed,
            responses.Count(response => response.Status is 200 or 201),
            [.. responses.Where(response => response.Status is not (200 or 201))],
            deadlocks,
            lockWaits
        );
    }

    private static async Task ReportCaseAsync(string caseName, LoadResult result, long persisted)
    {
        double documentsPerSecond =
            result.Elapsed.TotalSeconds > 0 ? result.Accepted / result.Elapsed.TotalSeconds : 0;

        await TestContext.Out.WriteLineAsync(
            $"=== {caseName}: {TotalAttempts} attempts in {result.Elapsed.TotalSeconds:F1}s "
                + $"({documentsPerSecond:F2} documents/sec); {result.Accepted} accepted, "
                + $"{result.Failures.Count} failed; {persisted} persisted; "
                + $"LCK[_]% delta {result.LockWaits} ==="
        );

        // Restated per case as a multiset with counts, so each case's block stands on its own and the
        // signatures read the way Gate B compares them.
        if (result.Deadlocks.IsInconclusive)
        {
            await TestContext.Out.WriteLineAsync(
                $"--- {caseName} deadlock signatures: INCONCLUSIVE: {result.Deadlocks.InconclusiveReason} ---"
            );
        }
        else
        {
            await TestContext.Out.WriteLineAsync(
                $"--- {caseName} deadlock signatures ({result.Deadlocks.Signatures.Count}) ---"
            );
            foreach (
                var group in result
                    .Deadlocks.Signatures.GroupBy(signature => signature, StringComparer.Ordinal)
                    .OrderByDescending(group => group.Count())
            )
            {
                await TestContext.Out.WriteLineAsync($"    x{group.Count()} {group.Key}");
            }
        }

        await ReportFailuresAsync($"--- {caseName} failures ---", result.Failures);
    }

    /// <summary>
    /// Records the leased database's isolation configuration next to the numbers, and requires the
    /// one setting the measurement depends on, so a baseline is known comparable to production
    /// rather than assumed to be. Reporting alone would leave that to whoever reads the log, and
    /// these numbers are read long after the run that produced them.
    /// </summary>
    private async Task ReportLeasedDatabaseConfigurationAsync()
    {
        bool readCommittedSnapshotOn;
        string snapshotIsolationState;

        await using (var command = Harness.DbConnection.CreateCommand())
        {
            command.CommandText = """
                SELECT [name], [compatibility_level], [is_read_committed_snapshot_on],
                       [snapshot_isolation_state_desc]
                FROM sys.databases
                WHERE [database_id] = DB_ID();
                """;

            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync())
                .Should()
                .BeTrue("sys.databases must describe the leased database this load runs against");

            await TestContext.Out.WriteLineAsync(
                $"--- leased database {reader.GetValue(0)}: "
                    + $"compatibility_level={Convert.ToInt32(reader.GetValue(1))}, "
                    + $"is_read_committed_snapshot_on={Convert.ToBoolean(reader.GetValue(2))}, "
                    + $"snapshot_isolation_state_desc={reader.GetValue(3)} ---"
            );

            readCommittedSnapshotOn = Convert.ToBoolean(reader.GetValue(2));
            snapshotIsolationState = Convert.ToString(reader.GetValue(3)) ?? "";
        }

        // Asserted for the same reason the empty-dms.Descriptor precondition above is: a leased
        // database that never received MatchProductionWriteIsolation runs at an isolation
        // configuration production never uses, and every number this fixture reports would then
        // describe a workload that is not the one under test - silently, and identically to a
        // healthy run.
        //
        // Both settings, because MatchProductionWriteIsolation applies them on two separate branches
        // and RCSI is also enabled on its own by EnableDocumentCacheReadAcceleration. Asserting only
        // RCSI would therefore pass on a lease that received it for the other reason and never
        // received the snapshot-isolation half - leaving exactly the reported-but-unverified gap
        // this method exists to close.
        using (new AssertionScope())
        {
            readCommittedSnapshotOn
                .Should()
                .BeTrue(
                    "production provisioning enables READ_COMMITTED_SNAPSHOT, so a load measured "
                        + "without it is not comparable to the field workload this fixture exists to model"
                );

            snapshotIsolationState
                .Should()
                .Be(
                    "ON",
                    "production provisioning also enables ALLOW_SNAPSHOT_ISOLATION, and a lease "
                        + "missing it did not receive MatchProductionWriteIsolation"
                );
        }
    }

    /// <summary>
    /// One namespace for every resource in the load, with a globally unique code value. Descriptor
    /// identity is namespace plus code value within a resource, so unique code values keep all 400
    /// creates distinct while the shared namespace makes the persisted count a single equality check.
    /// </summary>
    private static JsonObject BuildDescriptor(string ns, string suffix, int worker, int index)
    {
        string codeValue = $"{suffix}-{worker:D2}-{index:D3}";
        return new JsonObject
        {
            ["namespace"] = ns,
            ["codeValue"] = codeValue,
            ["shortDescription"] = codeValue,
        };
    }

    private async Task<long> CountAllDescriptorsAsync()
    {
        await using var command = Harness.DbConnection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(1) FROM [dms].[Descriptor];";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<long> CountDescriptorsInNamespaceAsync(string ns)
    {
        await using var command = Harness.DbConnection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(1) FROM [dms].[Descriptor] WHERE [Namespace] = @ns;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@ns";
        parameter.Value = ns;
        command.Parameters.Add(parameter);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Scoped to this run's identifier prefix rather than counting the whole table, so the count
    /// compares against this load's accepted responses without depending on the leased database
    /// holding no ChartOfAccount rows of its own.
    /// </summary>
    private async Task<long> CountPersistedChartOfAccountsAsync(string suffix)
    {
        await using var command = Harness.DbConnection.CreateCommand();
        command.CommandText =
            "SELECT COUNT_BIG(1) FROM [edfi].[ChartOfAccount] WHERE [AccountIdentifier] LIKE @prefix;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@prefix";
        parameter.Value = $"{suffix}-%";
        command.Parameters.Add(parameter);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
