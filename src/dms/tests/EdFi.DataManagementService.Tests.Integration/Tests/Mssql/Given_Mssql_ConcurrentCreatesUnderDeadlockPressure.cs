// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using FluentAssertions.Execution;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// Reproduction for the SQL Server deadlock misclassification: a transient, server-side database
/// failure must never be reported to the client as a 4xx client error, because well-behaved clients
/// do not retry 4xx and the document is then lost without a signal.
///
/// Drives concurrent ChartOfAccount creates through the real HTTP pipeline, classifies every
/// response, and reports what the database actually persisted.
/// </summary>
public sealed class Given_Mssql_ConcurrentCreatesUnderDeadlockPressure : MssqlConcurrentWriteLoadTestBase
{
    private const int Workers = 16;
    private const int PerWorker = 25;
    private const int TotalAttempts = Workers * PerWorker;

    [Test]
    [Explicit("Concurrency load reproduction; drives a deliberate deadlock storm.")]
    public async Task It_never_reports_a_transient_database_failure_as_a_client_error()
    {
        AllowRequestsToOutlastDefaultClientTimeout();

        string suffix = Guid.NewGuid().ToString("N")[..8];
        long schoolId = 1_000_000_000L + (Convert.ToInt64(suffix, 16) % 1_000_000_000L);
        string ns = $"uri://ed-fi.org/Dms1400/{suffix}";

        await SeedFixtureAsync(ns, schoolId, suffix);

        int deadlockGraphsBefore = await CountDeadlockGraphsAsync();

        ConcurrentBag<(int Status, string Body)> responses = [];

        await Task.WhenAll(
            Enumerable
                .Range(0, Workers)
                .Select(worker =>
                    Task.Run(async () =>
                    {
                        for (int i = 0; i < PerWorker; i++)
                        {
                            var (status, body) = await PostAsync(
                                "/data/ed-fi/chartOfAccounts",
                                BuildChartOfAccount(ns, schoolId, $"{suffix}-{worker:D2}-{i:D3}", worker, i)
                            );
                            responses.Add(((int)status, body));
                        }
                    })
                )
        );

        int accepted = responses.Count(response => response.Status is 200 or 201);
        var failures = responses.Where(response => response.Status is not (200 or 201)).ToArray();
        long persisted = await CountPersistedChartOfAccountsAsync();
        int deadlockGraphs = await CountDeadlockGraphsAsync() - deadlockGraphsBefore;

        await ReportFailuresAsync(
            $"=== {TotalAttempts} attempts: {accepted} accepted, {failures.Length} failed; "
                + $"{persisted} rows in edfi.ChartOfAccount; {deadlockGraphs} deadlock graph(s) ===",
            failures
        );

        var clientErrors = failures.Where(failure => failure.Status is >= 400 and < 500).ToArray();

        using (new AssertionScope())
        {
            // Without this the reproduction proves nothing: a run that produced no contention at all
            // satisfies the client-error assertion below exactly as a fully fixed one does, so the
            // test would keep passing if the load stopped reaching the code path under test.
            deadlockGraphs
                .Should()
                .BeGreaterThan(
                    0,
                    "the load must actually make SQL Server resolve deadlocks for the absence of "
                        + "client errors to mean anything"
                );

            persisted
                .Should()
                .Be(
                    accepted,
                    "every accepted create must be durable; a gap is the silent document loss this "
                        + "reproduction exists to detect"
                );

            // A deadlock victim (SQL Server 1205) is retriable and server-side. Reporting it - or any
            // other transient database failure - as a 4xx tells the client its request was malformed,
            // so the client will not retry and the document is silently dropped.
            clientErrors
                .Should()
                .BeEmpty(
                    "a transient database failure must not be reported as a client error, but "
                        + $"{clientErrors.Length} of {TotalAttempts} attempts returned 4xx: "
                        + string.Join(
                            " | ",
                            clientErrors.Select(SignatureOf).Distinct(StringComparer.Ordinal)
                        )
                );
        }
    }

    private async Task<long> CountPersistedChartOfAccountsAsync()
    {
        await using var command = Harness.DbConnection.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(1) FROM [edfi].[ChartOfAccount];";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task SeedFixtureAsync(string ns, long schoolId, string suffix)
    {
        await SeedCoreDescriptorsAsync(ns);

        var (status, body) = await PostAsync(
            "/data/ed-fi/schools",
            BuildSchool(ns, schoolId, suffix, documentPath: null)
        );
        status.Should().Be(HttpStatusCode.Created, $"the load needs a school to reference, but got: {body}");
    }
}
