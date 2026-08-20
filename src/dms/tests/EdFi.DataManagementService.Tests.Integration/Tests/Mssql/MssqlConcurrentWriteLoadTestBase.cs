// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// A <c>LCK[_]%</c> wait-stats reading: how many tasks waited on a lock, and how long they waited in
/// total. Named rather than a tuple because the two are both counters of the same shape and Gate C
/// reads them against different thresholds, so transposing them would produce a plausible number.
/// </summary>
public sealed record LockWaitTotals(long WaitingTasks, long WaitTimeMs)
{
    /// <summary>
    /// The growth from <paramref name="before"/> to this reading. Only this load's work when nothing
    /// else runs against the instance; the underlying counters are instance-wide and cumulative.
    /// </summary>
    public LockWaitTotals Since(LockWaitTotals before) =>
        new(WaitingTasks - before.WaitingTasks, WaitTimeMs - before.WaitTimeMs);

    public override string ToString() => $"{WaitingTasks} lock wait(s), {WaitTimeMs} ms";
}

/// <summary>
/// Shared machinery for the concurrency reproductions that drive deliberate lock contention through
/// the real HTTP pipeline. Both run against a leased database configured the way production
/// provisioning configures it, classify every response, and report what the database actually
/// persisted alongside what the engine recorded.
///
/// <para>Owns the Extended Events capture session's lifecycle; reading the XML it produces belongs
/// to <see cref="DeadlockGraphReader"/>, which needs no database and is unit-tested on its own.</para>
/// </summary>
public abstract class MssqlConcurrentWriteLoadTestBase : MssqlApiIntegrationTestBase
{
    private const string Json = "application/json";

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    /// <summary>
    /// Production provisioning enables RCSI and snapshot isolation; without them the locking
    /// behavior under test would run under an isolation configuration production never uses. The
    /// base class reverts it when the lease is released.
    /// </summary>
    protected override bool MatchProductionWriteIsolation => true;

    /// <summary>
    /// The load deliberately starves the server, so a single request can outlast the default
    /// 100-second client timeout while it waits on locks and replays contention. A client-side
    /// cancellation would be indistinguishable from the failure under test.
    /// </summary>
    protected void AllowRequestsToOutlastDefaultClientTimeout() =>
        Harness.HttpClient.Timeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Reduces a problem-details body to its identifying fields so responses that differ only by
    /// correlationId group together.
    /// </summary>
    protected static string SignatureOf((int Status, string Body) failure)
    {
        JsonNode? body = TryParse(failure.Body);
        if (body is null)
        {
            return failure.Body;
        }

        string type = body["type"]?.GetValue<string>() ?? "(no type)";
        string errors = string.Join(
            "; ",
            (body["validationErrors"] as JsonObject ?? []).Select(pair =>
                $"{pair.Key}={pair.Value?.ToJsonString()}"
            )
        );

        return errors.Length == 0 ? type : $"{type} {errors}";
    }

    private static JsonNode? TryParse(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    protected static async Task ReportFailuresAsync(
        string header,
        IReadOnlyCollection<(int Status, string Body)> failures
    )
    {
        await TestContext.Out.WriteLineAsync(header);

        foreach (
            var group in failures
                .GroupBy(failure => (failure.Status, Signature: SignatureOf(failure)))
                .OrderByDescending(group => group.Count())
        )
        {
            await TestContext.Out.WriteLineAsync(
                $"--- status {group.Key.Status} x{group.Count()} --- {group.Key.Signature}"
            );
            await TestContext.Out.WriteLineAsync($"    {group.First().Body}");
        }
    }

    /// <summary>
    /// The system_health ring buffer is server-wide and survives across tests, so only the growth
    /// over a load is attributable to it. That growth is a lower bound: the buffer is bounded and
    /// evicts oldest-first, so a storm large enough to fill it under-reports itself. Useful for
    /// "did deadlocks happen at all", not for counting them.
    /// </summary>
    protected static async Task<int> CountDeadlockGraphsAsync()
    {
        await using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString("master")
        );
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(1)
            FROM (
                SELECT CAST(xet.target_data AS XML) AS target_data
                FROM sys.dm_xe_session_targets xet
                JOIN sys.dm_xe_sessions xes ON xes.address = xet.event_session_address
                WHERE xes.name = 'system_health' AND xet.target_name = 'ring_buffer'
            ) AS ring
            CROSS APPLY ring.target_data.nodes('//RingBufferTarget/event[@name="xml_deadlock_report"]')
                AS t(node);
            """;

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Comfortably above the ~4 MB the ring buffer target can render into <c>target_data</c>, so an
    /// incomplete capture reports itself through <c>truncated</c> rather than through eviction.
    /// </summary>
    private const int DeadlockRingBufferMaxMemoryKb = 16384;

    /// <summary>
    /// Events sit in session buffers until dispatch. The session asks for the one-second minimum, so
    /// this is the wait that makes the read see the load's last graphs.
    /// </summary>
    private static readonly TimeSpan DeadlockDispatchDrainDelay = TimeSpan.FromSeconds(2);

    private string? _deadlockSessionName;

    /// <summary>
    /// Creates and starts an Extended Events session that belongs to this run alone, so its ring
    /// buffer cannot contain another run's graphs. <see cref="CountDeadlockGraphsAsync"/> reads the
    /// server-wide <c>system_health</c> buffer instead, which is enough to answer "did deadlocks
    /// happen at all" but cannot attribute a graph to a run; a differential signature comparison
    /// needs attribution, so it gets its own session rather than a filter over a shared one.
    /// </summary>
    protected async Task StartDeadlockCaptureAsync()
    {
        if (_deadlockSessionName is not null)
        {
            throw new InvalidOperationException(
                "A deadlock capture session is already running for this test."
            );
        }

        // Recorded before the session exists so a failure between CREATE and START still leaves a
        // name for the teardown to drop.
        _deadlockSessionName = $"dms1381_{Guid.NewGuid():N}"[..24];
        string quotedSessionName = MssqlTestDatabaseHelper.QuoteIdentifier(_deadlockSessionName);

        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"""
            CREATE EVENT SESSION {quotedSessionName} ON SERVER
                ADD EVENT sqlserver.xml_deadlock_report
                ADD TARGET package0.ring_buffer
                    (SET max_memory = {DeadlockRingBufferMaxMemoryKb}, max_events_limit = 0)
                WITH (MAX_DISPATCH_LATENCY = 1 SECONDS, STARTUP_STATE = OFF);
            ALTER EVENT SESSION {quotedSessionName} ON SERVER STATE = START;
            """
        );
    }

    /// <summary>
    /// Reads this run's graphs, reports them, and drops the session. Reading has to happen while the
    /// session is still running: <c>sys.dm_xe_sessions</c> lists running sessions only, so stopping
    /// it first would make <c>target_data</c> unreadable.
    /// </summary>
    protected async Task<DeadlockCapture> CaptureDeadlockSignaturesAsync()
    {
        if (_deadlockSessionName is null)
        {
            throw new InvalidOperationException(
                $"{nameof(StartDeadlockCaptureAsync)} must run before the load for its graphs to be captured."
            );
        }

        string sessionName = _deadlockSessionName;
        await Task.Delay(DeadlockDispatchDrainDelay);

        var (targetData, droppedEvents) = await ReadDeadlockRingBufferAsync(sessionName);
        await StopDeadlockCaptureAsync();

        DeadlockCapture capture = targetData is null
            ? new DeadlockCapture(
                [],
                [],
                0,
                $"Extended Events session '{sessionName}' was not running when its target was read"
            )
            : DeadlockGraphReader.CaptureFromRingBufferTarget(targetData, Harness.DbConnection.Database);

        if (droppedEvents > 0 && !capture.IsInconclusive)
        {
            capture = capture with
            {
                Signatures = [],
                InconclusiveReason =
                    $"the capture session dropped {droppedEvents} event(s) before they reached its target",
            };
        }

        await ReportDeadlockCaptureAsync(sessionName, capture);
        return capture;
    }

    /// <summary>
    /// Drops the session if the test never reached <see cref="CaptureDeadlockSignaturesAsync"/>. An
    /// Extended Events session is server-scoped, so a leaked one outlives the leased database and
    /// keeps recording for every later test on the instance.
    /// </summary>
    [TearDown]
    public Task DropLeakedDeadlockCaptureSessionAsync() => StopDeadlockCaptureAsync();

    private async Task StopDeadlockCaptureAsync()
    {
        if (_deadlockSessionName is null)
        {
            return;
        }

        string quotedSessionName = MssqlTestDatabaseHelper.QuoteIdentifier(_deadlockSessionName);
        string escapedSessionName = MssqlTestDatabaseHelper.EscapeSqlLiteral(_deadlockSessionName);
        _deadlockSessionName = null;

        // DROP stops a running session, so there is no separate STATE = STOP step to get wrong -
        // stopping an already-stopped session is an error.
        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"""
            IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE [name] = N'{escapedSessionName}')
            BEGIN
                DROP EVENT SESSION {quotedSessionName} ON SERVER;
            END
            """
        );
    }

    private static async Task<(string? TargetData, long DroppedEvents)> ReadDeadlockRingBufferAsync(
        string sessionName
    )
    {
        await using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString("master")
        );
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(xet.target_data AS nvarchar(max)), xes.dropped_event_count
            FROM sys.dm_xe_sessions xes
            INNER JOIN sys.dm_xe_session_targets xet
                ON xet.event_session_address = xes.address
            WHERE xes.[name] = @sessionName AND xet.target_name = N'ring_buffer';
            """;
        command.Parameters.Add(new SqlParameter("@sessionName", sessionName));

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return (null, 0);
        }

        return (
            await reader.IsDBNullAsync(0) ? null : reader.GetString(0),
            await reader.IsDBNullAsync(1) ? 0 : Convert.ToInt64(reader.GetValue(1))
        );
    }

    private static async Task ReportDeadlockCaptureAsync(string sessionName, DeadlockCapture capture)
    {
        await TestContext.Out.WriteLineAsync(
            $"=== deadlock capture '{sessionName}': {capture.Graphs.Count} graph(s), "
                + $"{capture.AttributedGraphCount} attributed to the leased database, "
                + $"{capture.Signatures.Count} signature(s) ==="
        );

        if (capture.IsInconclusive)
        {
            await TestContext.Out.WriteLineAsync($"--- INCONCLUSIVE: {capture.InconclusiveReason} ---");
        }

        foreach (string signature in capture.Signatures)
        {
            await TestContext.Out.WriteLineAsync($"    {signature}");
        }

        // The raw graphs are the evidence, and they have to outlive the normalizer that a
        // differential comparison is trusting.
        foreach (string graph in capture.Graphs)
        {
            await TestContext.Out.WriteLineAsync(graph);
        }
    }

    /// <summary>
    /// Both halves of the <c>LCK[_]%</c> reading: how many tasks waited on a lock, and how long they
    /// waited in total. Both, because waiting-task count alone does not describe contention - a load
    /// can trade many short waits for few long ones and leave the count flat - and wait <em>time</em>
    /// is the field metric: the Northridge run's headline number was <c>LCK_M_U</c> at 4,339,447 ms
    /// across 4,086 waits, 38x the tempdb latch time.
    ///
    /// <para>The growth over a load is a contention signal independent of what any request returned:
    /// contention the retry pipeline absorbed into successful responses still shows up here, which is
    /// what makes it usable as the precondition for an assertion about response status.</para>
    ///
    /// <para>Read it as a floor, not as attribution. These are instance-wide counters, cumulative
    /// across every database on the server, so a delta is only this load's work when nothing else is
    /// running against the instance - true for these reproductions, which are
    /// <see cref="ExplicitAttribute"/> and driven one at a time, but not a property the query can
    /// enforce.</para>
    /// </summary>
    protected static async Task<LockWaitTotals> CaptureLockWaitsAsync()
    {
        await using var connection = new SqlConnection(
            MssqlTestDatabaseHelper.BuildConnectionString("master")
        );
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ISNULL(SUM(waiting_tasks_count), 0), ISNULL(SUM(wait_time_ms), 0)
            FROM sys.dm_os_wait_stats
            WHERE wait_type LIKE 'LCK[_]%';
            """;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                "The LCK[_]% wait-stats aggregate returned no row; sys.dm_os_wait_stats was unreadable."
            );
        }

        return new LockWaitTotals(reader.GetInt64(0), reader.GetInt64(1));
    }

    protected async Task SeedCoreDescriptorsAsync(string ns)
    {
        await SeedDescriptorAsync(
            "/data/ed-fi/educationOrganizationCategoryDescriptors",
            $"{ns}/EducationOrganizationCategoryDescriptor",
            "School"
        );
        await SeedDescriptorAsync(
            "/data/ed-fi/gradeLevelDescriptors",
            $"{ns}/GradeLevelDescriptor",
            "Tenth grade"
        );
        await SeedDescriptorAsync(
            "/data/ed-fi/accountTypeDescriptors",
            $"{ns}/AccountTypeDescriptor",
            "Revenue"
        );
        await SeedDescriptorAsync(
            "/data/ed-fi/reportingTagDescriptors",
            $"{ns}/ReportingTagDescriptor",
            "ESSA"
        );
    }

    /// <summary>
    /// Asserted rather than fire-and-forget: a descriptor the load's payloads reference but the
    /// seed failed to create makes every request in the load fail validation with a 400, which is
    /// indistinguishable at the assertion from the defect these reproductions exist to detect.
    /// </summary>
    private async Task SeedDescriptorAsync(string endpoint, string namespaceUri, string codeValue)
    {
        var (status, body) = await PostAsync(
            endpoint,
            new JsonObject
            {
                ["namespace"] = namespaceUri,
                ["codeValue"] = codeValue,
                ["shortDescription"] = codeValue,
            }
        );

        status
            .Should()
            .Be(
                HttpStatusCode.Created,
                $"the load's payloads reference {namespaceUri}#{codeValue}, but seeding it returned: {body}"
            );
    }

    protected static JsonObject BuildSchool(string ns, long schoolId, string name, string? documentPath)
    {
        var school = new JsonObject
        {
            ["schoolId"] = schoolId,
            ["nameOfInstitution"] = $"DMS-1400 School {name}",
            ["educationOrganizationCategories"] = new JsonArray(
                new JsonObject
                {
                    ["educationOrganizationCategoryDescriptor"] =
                        $"{ns}/EducationOrganizationCategoryDescriptor#School",
                }
            ),
            ["gradeLevels"] = new JsonArray(
                new JsonObject { ["gradeLevelDescriptor"] = $"{ns}/GradeLevelDescriptor#Tenth grade" }
            ),
        };

        AddIdFromPath(school, documentPath);
        return school;
    }

    protected static JsonObject BuildChartOfAccount(
        string ns,
        long schoolId,
        string accountIdentifier,
        int worker,
        int index,
        string? documentPath = null
    )
    {
        var chartOfAccount = new JsonObject
        {
            ["accountIdentifier"] = accountIdentifier,
            ["fiscalYear"] = 2024,
            ["educationOrganizationReference"] = new JsonObject { ["educationOrganizationId"] = schoolId },
            ["accountTypeDescriptor"] = $"{ns}/AccountTypeDescriptor#Revenue",
            ["accountName"] = $"Account {worker}-{index}",
            ["reportingTags"] = new JsonArray(
                new JsonObject
                {
                    ["reportingTagDescriptor"] = $"{ns}/ReportingTagDescriptor#ESSA",
                    ["tagValue"] = $"tag-{worker}-{index}",
                }
            ),
        };

        AddIdFromPath(chartOfAccount, documentPath);
        return chartOfAccount;
    }

    /// <summary>PUT requires the document id in the body; it is the last path segment.</summary>
    private static void AddIdFromPath(JsonObject document, string? documentPath)
    {
        if (documentPath is not null)
        {
            document["id"] = documentPath[(documentPath.LastIndexOf('/') + 1)..];
        }
    }

    protected async Task<(HttpStatusCode Status, string Body)> PostAsync(string endpoint, JsonObject payload)
    {
        var (status, body, _) = await PostWithLocationAsync(endpoint, payload);
        return (status, body);
    }

    protected async Task<(HttpStatusCode Status, string Body, string? Location)> PostWithLocationAsync(
        string endpoint,
        JsonObject payload
    )
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, Json);
        using HttpResponseMessage response = await Harness.HttpClient.PostAsync(endpoint, content);
        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            response.Headers.Location?.ToString()
        );
    }

    protected async Task<(HttpStatusCode Status, string Body)> PutAsync(string path, JsonObject payload)
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, Json);
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = content };
        using HttpResponseMessage response = await Harness.HttpClient.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
