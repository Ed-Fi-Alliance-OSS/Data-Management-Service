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
/// Targets the write executor's first phase specifically. The first phase captures and locks the
/// target, runs stored authorization, resolves references and hydrates state, and it reaches the
/// database before any execution request has been resolved - so a DbException raised there has no
/// resolved request to attribute a write failure to. Transient failures are mapped to the same
/// write conflict a second-phase deadlock produces and retried; failures whose outcome is
/// indeterminate are reported as server errors; anything else still surfaces unmapped. This drives
/// contention onto first-phase statements by mixing updates of a shared referenced document with
/// inserts that resolve a reference to it, and asserts that none of those paths can hand the client
/// a 4xx or raw engine text.
/// </summary>
public sealed class Given_Mssql_FirstPhaseContentionUnderConcurrentWrites : MssqlConcurrentWriteLoadTestBase
{
    // Overridable so the contention shape can be tuned without a rebuild: a deep queue on one hot
    // row produces lock-wait timeouts, while a shallow one lets genuine cycles form.
    private static readonly int UpdateWorkers = Tunable("DMS1400_UPDATE_WORKERS", 6);
    private static readonly int InsertWorkers = Tunable("DMS1400_INSERT_WORKERS", 6);
    private static readonly int ChildUpdateWorkers = Tunable("DMS1400_CHILD_UPDATE_WORKERS", 4);
    private static readonly int Iterations = Tunable("DMS1400_ITERATIONS", 30);
    private static readonly int SharedChildCount = Tunable("DMS1400_SHARED_CHILDREN", 4);

    private static int Tunable(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0 ? value : fallback;

    [Test]
    [Explicit("Concurrency load reproduction; drives a deliberate deadlock storm.")]
    public async Task It_never_reports_a_first_phase_database_failure_as_a_client_error()
    {
        AllowRequestsToOutlastDefaultClientTimeout();

        string suffix = Guid.NewGuid().ToString("N")[..8];
        long schoolId = 1_000_000_000L + (Convert.ToInt64(suffix, 16) % 1_000_000_000L);
        string ns = $"uri://ed-fi.org/Dms1400First/{suffix}";

        await SeedCoreDescriptorsAsync(ns);
        string schoolPath = await CreateSchoolAsync(ns, schoolId, suffix);
        string[] childPaths = await CreateSharedChildrenAsync(ns, schoolId, suffix);

        long lockWaitsBefore = await CountLockWaitsAsync();

        ConcurrentBag<(int Status, string Body)> responses = [];

        var updateSchool = Enumerable
            .Range(0, UpdateWorkers)
            .Select(worker =>
                Task.Run(async () =>
                {
                    for (int i = 0; i < Iterations; i++)
                    {
                        responses.Add(
                            Tuple(
                                await PutAsync(
                                    schoolPath,
                                    BuildSchool(ns, schoolId, $"{suffix} rev {worker}-{i}", schoolPath)
                                )
                            )
                        );
                    }
                })
            );

        var insertChildren = Enumerable
            .Range(0, InsertWorkers)
            .Select(worker =>
                Task.Run(async () =>
                {
                    for (int i = 0; i < Iterations; i++)
                    {
                        responses.Add(
                            Tuple(
                                await PostAsync(
                                    "/data/ed-fi/chartOfAccounts",
                                    BuildChartOfAccount(
                                        ns,
                                        schoolId,
                                        $"{suffix}-i{worker:D2}-{i:D3}",
                                        worker,
                                        i
                                    )
                                )
                            )
                        );
                    }
                })
            );

        var updateChildren = Enumerable
            .Range(0, ChildUpdateWorkers)
            .Select(worker =>
                Task.Run(async () =>
                {
                    for (int i = 0; i < Iterations; i++)
                    {
                        string path = childPaths[(worker + i) % childPaths.Length];
                        responses.Add(
                            Tuple(
                                await PutAsync(
                                    path,
                                    BuildChartOfAccount(
                                        ns,
                                        schoolId,
                                        $"{suffix}-shared-{(worker + i) % childPaths.Length:D2}",
                                        worker,
                                        i,
                                        path
                                    )
                                )
                            )
                        );
                    }
                })
            );

        await Task.WhenAll(updateSchool.Concat(insertChildren).Concat(updateChildren));

        var failures = responses.Where(response => response.Status is not (200 or 201 or 204)).ToArray();
        long lockWaits = await CountLockWaitsAsync() - lockWaitsBefore;

        await ReportFailuresAsync(
            $"=== {responses.Count} attempts, {failures.Length} failed; {lockWaits} lock wait(s) ===",
            failures
        );

        var engineTextLeaks = failures
            .Where(failure =>
                failure.Body.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
                || failure.Body.Contains("Rerun the transaction", StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();

        var clientErrors = failures.Where(failure => failure.Status is >= 400 and < 500).ToArray();

        using (new AssertionScope())
        {
            // A floor on contention, not proof of it. Without this a run whose workers never
            // collided satisfies both assertions below exactly as a fully fixed one does. Measured
            // on the server rather than inferred from responses, so it neither passes on an
            // unrelated failure nor fails when the retry pipeline absorbs contention into
            // successes.
            //
            // What it does not establish: that any first-phase statement actually raised a
            // DbException. Writers can wait on locks and every one of them still succeed, and no
            // signal visible to an HTTP client separates that from a run where the mapping under
            // test was exercised. Read the printed report, not just the green result.
            lockWaits
                .Should()
                .BeGreaterThan(
                    0,
                    "the load must actually make writers wait on each other's locks for the absence "
                        + "of client errors to mean anything"
                );

            engineTextLeaks
                .Should()
                .BeEmpty("the raw database error text must never reach the client response body");

            clientErrors
                .Should()
                .BeEmpty(
                    "a transient database failure must not be reported as a client error, but "
                        + $"{clientErrors.Length} of {responses.Count} attempts returned 4xx: "
                        + string.Join(
                            " | ",
                            clientErrors.Select(SignatureOf).Distinct(StringComparer.Ordinal)
                        )
                );
        }
    }

    private static (int Status, string Body) Tuple((HttpStatusCode Status, string Body) response) =>
        ((int)response.Status, response.Body);

    private async Task<string> CreateSchoolAsync(string ns, long schoolId, string suffix)
    {
        var (status, body, location) = await PostWithLocationAsync(
            "/data/ed-fi/schools",
            BuildSchool(ns, schoolId, suffix, documentPath: null)
        );

        status.Should().Be(HttpStatusCode.Created, body);
        return location!;
    }

    private async Task<string[]> CreateSharedChildrenAsync(string ns, long schoolId, string suffix)
    {
        List<string> paths = [];

        for (int i = 0; i < SharedChildCount; i++)
        {
            var (status, body, location) = await PostWithLocationAsync(
                "/data/ed-fi/chartOfAccounts",
                BuildChartOfAccount(ns, schoolId, $"{suffix}-shared-{i:D2}", 0, i)
            );

            status.Should().Be(HttpStatusCode.Created, body);
            paths.Add(location!);
        }

        return [.. paths];
    }
}
