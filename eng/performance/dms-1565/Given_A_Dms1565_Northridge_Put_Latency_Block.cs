// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// DMS-1565 measurement block (local campaign tooling, not a regression test). Hosts the production DMS HTTP
/// pipeline in process against an externally restored Northridge database and the staged core-only ApiSchema,
/// then, per resource: prepares bodies with GETs, runs warmup PUTs, snapshots Query Store, runs the measured
/// PUTs with client-side timing, snapshots again, and asserts one root UPDATE per measured request. All
/// configuration comes from DMS1565_* environment variables; the output is one JSON file per block.
/// </summary>
[TestFixture]
[Explicit("DMS-1565 measurement campaign block; driven by the campaign script.")]
public class Given_A_Dms1565_Northridge_Put_Latency_Block : MssqlApiIntegrationTestBase
{
    private static readonly Dictionary<string, (string Table, string Field, bool Increment)> _resources =
        new()
        {
            ["sessions"] = ("Session", "totalInstructionalDays", true),
            ["courseOfferings"] = ("CourseOffering", "localCourseTitle", false),
            ["studentSchoolAssociations"] = ("StudentSchoolAssociation", "repeatGradeIndicator", false),
            ["classPeriods"] = ("ClassPeriod", "officialAttendancePeriod", false),
            ["sections"] = ("Section", "sectionName", false),
        };

    private static readonly Dictionary<string, JsonNode> _setValues = new()
    {
        ["localCourseTitle"] = JsonValue.Create("DMS-1565 measured title")!,
        ["repeatGradeIndicator"] = JsonValue.Create(true)!,
        ["officialAttendancePeriod"] = JsonValue.Create(true)!,
        ["sectionName"] = JsonValue.Create("DMS-1565 measured name")!,
    };

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool ResourceLinksEnabled => false;

    protected override IReadOnlyDictionary<string, string> AdditionalHostSettings =>
        new Dictionary<string, string> { ["AppSettings:ApiSchema:Path"] = Required("DMS1565_APISCHEMA_DIR") };

    protected override Task<string> LeaseDatabaseAsync(FixtureContext fixture) =>
        Task.FromResult(Required("DMS1565_NR_CONNECTION"));

    protected override Task ReleaseDatabaseAsync(string leasedConnectionString) => Task.CompletedTask;

    protected override Task<string> LeaseAdditionalDatabaseAsync(FixtureContext fixture) =>
        throw new NotSupportedException();

    protected override Task ReleaseAdditionalDatabaseAsync(string leasedConnectionString) =>
        Task.CompletedTask;

    [Test]
    public async Task It_measures_one_block()
    {
        PublishPrimaryOwnership();
        var warmup = int.Parse(Required("DMS1565_WARMUP"), CultureInfo.InvariantCulture);
        var measured = int.Parse(Required("DMS1565_MEASURED"), CultureInfo.InvariantCulture);
        var order = Required("DMS1565_RESOURCE_ORDER").Split(',');
        var docList = JsonNode.Parse(await File.ReadAllTextAsync(Required("DMS1565_DOCLIST")))!.AsObject();
        var connection = Harness.DbConnection;
        var output = new JsonObject
        {
            ["arm"] = Required("DMS1565_ARM"),
            ["block"] = Required("DMS1565_BLOCK"),
            ["resourceOrder"] = new JsonArray([.. order.Select(r => (JsonNode)JsonValue.Create(r)!)]),
            ["warmup"] = warmup,
            ["measured"] = measured,
            ["startedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        using (var smoke = await Harness.HttpClient.GetAsync("/data/ed-fi/sessions?limit=1&totalCount=true"))
        {
            output["smokeStatus"] = (int)smoke.StatusCode;
            output["smokeTotalCount"] = smoke.Headers.TryGetValues("Total-Count", out var totals)
                ? totals.First()
                : null;
            Assert.That(smoke.StatusCode, Is.EqualTo(HttpStatusCode.OK), "smoke GET");
        }

        output["effectiveSchema"] = await QueryAsync(
            connection,
            "SELECT EffectiveSchemaHash, ResourceKeyCount FROM dms.EffectiveSchema;"
        );
        output["queryStoreStateAtStart"] = await QueryAsync(
            connection,
            "SELECT actual_state_desc, readonly_reason, query_capture_mode_desc FROM sys.database_query_store_options;"
        );

        var results = new JsonObject();

        foreach (var resource in order)
        {
            var (table, field, increment) = _resources[resource];
            var uuids = docList[resource]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
            var originals = new JsonObject[uuids.Length];

            for (var index = 0; index < uuids.Length; index++)
            {
                using var get = await Harness.HttpClient.GetAsync($"/data/ed-fi/{resource}/{uuids[index]}");
                Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"GET {resource}/{uuids[index]}");
                var body = JsonNode.Parse(await get.Content.ReadAsStringAsync())!.AsObject();
                body.Remove("_etag");
                body.Remove("_lastModifiedDate");
                originals[index] = body;
            }

            var visits = new int[uuids.Length];
            var requestIndex = 0;

            JsonObject NextBody(out string uuid)
            {
                var position = requestIndex++ % uuids.Length;
                uuid = uuids[position];
                var body = originals[position].DeepClone().AsObject();

                if (visits[position]++ % 2 == 0)
                {
                    if (increment)
                    {
                        body[field] = body[field]!.GetValue<int>() + 1;
                    }
                    else
                    {
                        body[field] = _setValues[field].DeepClone();
                    }
                }

                return body;
            }

            for (var i = 0; i < warmup; i++)
            {
                var body = NextBody(out var uuid);
                await PutAsync(resource, uuid, body);
            }

            var before = await SnapshotAsync(connection, table);
            var samples = new JsonArray();
            var started = DateTime.UtcNow;

            for (var i = 0; i < measured; i++)
            {
                var body = NextBody(out var uuid);
                samples.Add(await PutAsync(resource, uuid, body));
            }

            var finished = DateTime.UtcNow;
            var after = await SnapshotAsync(connection, table);
            var executionDelta = TotalExecutions(after) - TotalExecutions(before);

            results[resource] = new JsonObject
            {
                ["table"] = table,
                ["field"] = field,
                ["documents"] = uuids.Length,
                ["measuredStartedAtUtc"] = started.ToString("O", CultureInfo.InvariantCulture),
                ["measuredFinishedAtUtc"] = finished.ToString("O", CultureInfo.InvariantCulture),
                ["latencyMs"] = samples,
                ["rootUpdateExecutionDelta"] = executionDelta,
                ["queryStoreBefore"] = before,
                ["queryStoreAfter"] = after,
            };

            Assert.That(
                executionDelta,
                Is.EqualTo(measured),
                $"{resource}: expected one root UPDATE [edfi].[{table}] per measured PUT"
            );
        }

        output["results"] = results;
        output["queryStoreStateAtEnd"] = await QueryAsync(
            connection,
            "SELECT actual_state_desc, readonly_reason, query_capture_mode_desc FROM sys.database_query_store_options;"
        );
        output["finishedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await File.WriteAllTextAsync(
            Required("DMS1565_OUTPUT"),
            output.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
        );
    }

    /// <summary>
    /// Publishes data-store ownership for the configured primary, as the production configuration-service
    /// provider does after each load. The harness's stub provider never publishes it, so without this every
    /// SQL Server pool identity counts as retired and is cleared when its last lease ends, which costs fresh
    /// logins on every request. Applied identically in both arms.
    /// </summary>
    private void PublishPrimaryOwnership()
    {
        var snapshot = new DataStoreOwnershipSnapshot(
            1,
            ImmutableArray.Create(
                new ConfiguredTargetOwner(
                    string.Empty,
                    ExternalDoublesConstants.StableDataStoreId,
                    EffectiveTargetKind.Primary,
                    PrimaryConnectionString
                )
            )
        );

        foreach (var reconciler in Harness.Services.GetServices<IDataStoreOwnershipReconciler>())
        {
            reconciler.Reconcile(snapshot);
        }
    }

    private async Task<double> PutAsync(string resource, string uuid, JsonObject body)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var start = Stopwatch.GetTimestamp();
        using var response = await Harness.HttpClient.PutAsync($"/data/ed-fi/{resource}/{uuid}", content);
        var responseBody = await response.Content.ReadAsByteArrayAsync();
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.OK))
        {
            Assert.Fail(
                $"PUT {resource}/{uuid} returned {(int)response.StatusCode}: {Encoding.UTF8.GetString(responseBody)}"
            );
        }

        return elapsed;
    }

    /// <summary>
    /// Query Store state for the root UPDATE statements of one table: per-query compile counts and every
    /// runtime-stats row (all intervals and execution types) for their plans, plus instance compile counters.
    /// </summary>
    private static async Task<JsonObject> SnapshotAsync(DbConnection connection, string table)
    {
        await QueryAsync(connection, "EXEC sys.sp_query_store_flush_db;");
        var pattern = $"%UPDATE \\[edfi\\].\\[{table}\\]%";

        return new JsonObject
        {
            ["takenAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["queries"] = await QueryAsync(
                connection,
                $"""
                SELECT q.query_id, q.count_compiles, LEFT(qt.query_sql_text, 4000) AS query_sql_text
                FROM sys.query_store_query q
                JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
                WHERE q.object_id = 0 AND qt.query_sql_text LIKE N'{pattern}' ESCAPE '\';
                """
            ),
            ["runtime"] = await QueryAsync(
                connection,
                $"""
                SELECT p.plan_id, p.query_id, rs.execution_type, rs.runtime_stats_interval_id,
                    rs.count_executions, rs.avg_duration, rs.avg_cpu_time, rs.avg_logical_io_reads,
                    rs.avg_logical_io_writes
                FROM sys.query_store_runtime_stats rs
                JOIN sys.query_store_plan p ON p.plan_id = rs.plan_id
                JOIN sys.query_store_query q ON q.query_id = p.query_id
                JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
                WHERE q.object_id = 0 AND qt.query_sql_text LIKE N'{pattern}' ESCAPE '\';
                """
            ),
            ["counters"] = await QueryAsync(
                connection,
                """
                SELECT RTRIM(counter_name) AS counter_name, cntr_value
                FROM sys.dm_os_performance_counters
                WHERE counter_name IN ('SQL Compilations/sec', 'SQL Re-Compilations/sec', 'Batch Requests/sec');
                """
            ),
        };
    }

    private static long TotalExecutions(JsonObject snapshot) =>
        snapshot["runtime"]!.AsArray().Sum(row => row!["count_executions"]!.GetValue<long>());

    private static async Task<JsonArray> QueryAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new JsonArray();

        do
        {
            while (await reader.ReadAsync())
            {
                var row = new JsonObject();

                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    row[reader.GetName(ordinal)] = await reader.IsDBNullAsync(ordinal)
                        ? null
                        : reader.GetValue(ordinal) switch
                        {
                            byte[] bytes => JsonValue.Create(Convert.ToHexString(bytes)),
                            long value => JsonValue.Create(value),
                            int value => JsonValue.Create((long)value),
                            short value => JsonValue.Create((long)value),
                            double value => JsonValue.Create(value),
                            float value => JsonValue.Create((double)value),
                            decimal value => JsonValue.Create((double)value),
                            var value => JsonValue.Create(
                                Convert.ToString(value, CultureInfo.InvariantCulture)
                            ),
                        };
                }

                rows.Add(row);
            }
        } while (await reader.NextResultAsync());

        return rows;
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} for the DMS-1565 measurement block.");
}
