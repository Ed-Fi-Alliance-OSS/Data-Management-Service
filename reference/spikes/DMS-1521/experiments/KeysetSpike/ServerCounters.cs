// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace KeysetSpike;

/// <summary>One stable reading of the cumulative counters, with non-cumulative detail beside it.</summary>
public sealed record CounterSnapshot(DateTime At, SortedDictionary<string, double> Values, JsonObject Detail);

/// <summary>A workload's counter deltas, net of an idle window of equal length, and per request.</summary>
public sealed record CounterWindow(
    int Requests,
    double WorkloadSeconds,
    double IdleSeconds,
    SortedDictionary<string, double> WorkloadDelta,
    SortedDictionary<string, double> IdleDelta,
    SortedDictionary<string, double> Net,
    SortedDictionary<string, double> NetPerRequest,
    JsonObject DetailBefore,
    JsonObject DetailAfter
)
{
    public JsonObject ToJson() =>
        new()
        {
            ["requests"] = Requests,
            ["workloadSeconds"] = WorkloadSeconds,
            ["idleSeconds"] = IdleSeconds,
            ["workloadDelta"] = ServerCounters.ToJson(WorkloadDelta),
            ["idleDelta"] = ServerCounters.ToJson(IdleDelta),
            ["net"] = ServerCounters.ToJson(Net),
            ["netPerRequest"] = ServerCounters.ToJson(NetPerRequest),
            ["detailBefore"] = DetailBefore.DeepClone(),
            ["detailAfter"] = DetailAfter.DeepClone(),
        };
}

/// <summary>
/// Server counters read with the spike's flush and snapshot protocol:
/// <list type="number">
/// <item>The workload finishes; its connections close or go idle.</item>
/// <item>After at least 10 s of idle (PostgreSQL publishes pending backend statistics with a
/// delay), readings are taken on a fresh connection, outside an explicit transaction, after
/// <c>pg_stat_clear_snapshot()</c>, until two readings at least 1 s apart agree.</item>
/// <item>An idle window of the same length is measured the same way and subtracted.</item>
/// </list>
/// The readers run in a database other than the measured one (<c>postgres</c> for
/// <c>pg_stat_statements</c>, <c>master</c> on SQL Server) or are marked, so they do not count
/// themselves. PostgreSQL catalog churn is per database, so <c>pg_stat_sys_tables</c> is read in the
/// measured database; reading it inserts and deletes nothing.
/// </summary>
public sealed class ServerCounters : IAsyncDisposable
{
    public const string Marker = "/* dms1521-counters */";

    public static readonly IReadOnlyList<string> CatalogTables =
    [
        "pg_class",
        "pg_attribute",
        "pg_type",
        "pg_depend",
        "pg_index",
        "pg_constraint",
        "pg_statistic",
    ];

    private static readonly TimeSpan _flushIdle = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _agreementGap = TimeSpan.FromSeconds(1);
    private const int MaximumReadings = 90;

    private readonly PerfProvider _provider;
    private readonly string _measuredConnectionString;
    private readonly string _serverConnectionString;
    private readonly string _databaseName;
    private bool _xeSessionCreated;

    public ServerCounters(PerfProvider provider, string measuredConnectionString)
    {
        _provider = provider;
        _measuredConnectionString = measuredConnectionString;
        if (provider == PerfProvider.Postgresql)
        {
            NpgsqlConnectionStringBuilder builder = new(measuredConnectionString);
            _databaseName = builder.Database!;
            builder.Database = "postgres";
            builder.Pooling = false;
            _serverConnectionString = builder.ConnectionString;
        }
        else
        {
            SqlConnectionStringBuilder builder = new(measuredConnectionString);
            _databaseName = builder.InitialCatalog;
            builder.InitialCatalog = "master";
            builder.Pooling = false;
            _serverConnectionString = builder.ConnectionString;
        }
    }

    public static string XeSessionName => "dms1521_recompiles";

    /// <summary>The gate counters: the ones calibration must prove register a temp table.</summary>
    public IReadOnlyList<string> TempTableGateKeys =>
        _provider == PerfProvider.Postgresql
            ?
            [
                .. CatalogTables.SelectMany(table =>
                    new[] { $"catalog.{table}.n_tup_ins", $"catalog.{table}.n_tup_del" }
                ),
            ]
            // The server-wide Temp Tables Creation Rate also counts background temp tables (task 4),
            // so the gate is the session-filtered Extended Events count; the counter stays informational.
            : ["xe.temp_tables_created"];

    public async Task InitializeAsync()
    {
        if (_provider == PerfProvider.Postgresql)
        {
            await using NpgsqlConnection connection = new(_serverConnectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, "CREATE EXTENSION IF NOT EXISTS pg_stat_statements;");
            return;
        }

        await using SqlConnection server = new(_serverConnectionString);
        await server.OpenAsync();
        long databaseId = Convert.ToInt64(
            await ScalarAsync(server, $"SELECT DB_ID(N'{_databaseName.Replace("'", "''")}');"),
            CultureInfo.InvariantCulture
        );
        await ExecuteAsync(
            server,
            $"""
            IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'{XeSessionName}')
                DROP EVENT SESSION [{XeSessionName}] ON SERVER;
            CREATE EVENT SESSION [{XeSessionName}] ON SERVER
            ADD EVENT sqlserver.sql_statement_recompile (WHERE sqlserver.database_id = {databaseId}),
            -- Temp tables (USRTAB 8277, in tempdb, at DDL commit) created by sessions whose current
            -- database is the measured one; table variables and other databases' sessions are excluded.
            ADD EVENT sqlserver.object_created (
                WHERE sqlserver.database_id = {databaseId} AND database_id = 2 AND ddl_phase = 1 AND object_type = 8277)
            ADD TARGET package0.histogram (
                SET filtering_event_name = N'sqlserver.sql_statement_recompile',
                    source = N'recompile_cause',
                    source_type = 0),
            ADD TARGET package0.event_counter
            WITH (MAX_DISPATCH_LATENCY = 1 SECONDS, EVENT_RETENTION_MODE = NO_EVENT_LOSS);
            ALTER EVENT SESSION [{XeSessionName}] ON SERVER STATE = START;
            """
        );
        _xeSessionCreated = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_xeSessionCreated)
        {
            await using SqlConnection server = new(_serverConnectionString);
            await server.OpenAsync();
            await ExecuteAsync(
                server,
                $"IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'{XeSessionName}') DROP EVENT SESSION [{XeSessionName}] ON SERVER;"
            );
        }
    }

    /// <summary>
    /// Measures <paramref name="workloadAsync" /> (which returns its request count) between two
    /// stable readings, then an idle window of the same length, and reports the net per request.
    /// </summary>
    public async Task<CounterWindow> MeasureAsync(Func<Task<int>> workloadAsync)
    {
        CounterSnapshot before = await ReadStableAsync();
        int requests = await workloadAsync();
        CounterSnapshot after = await ReadStableAsync();
        TimeSpan workload = after.At - before.At;

        CounterSnapshot idleBefore = await ReadStableAsync();
        TimeSpan remaining = workload - (DateTime.UtcNow - idleBefore.At) - _flushIdle;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }

        CounterSnapshot idleAfter = await ReadStableAsync();
        SortedDictionary<string, double> workloadDelta = Subtract(after.Values, before.Values);
        SortedDictionary<string, double> idleDelta = Subtract(idleAfter.Values, idleBefore.Values);
        SortedDictionary<string, double> net = Subtract(workloadDelta, idleDelta);
        SortedDictionary<string, double> perRequest = new(
            net.ToDictionary(pair => pair.Key, pair => requests == 0 ? double.NaN : pair.Value / requests),
            StringComparer.Ordinal
        );
        return new CounterWindow(
            requests,
            workload.TotalSeconds,
            (idleAfter.At - idleBefore.At).TotalSeconds,
            workloadDelta,
            idleDelta,
            net,
            perRequest,
            before.Detail,
            after.Detail
        );
    }

    /// <summary>Waits out the flush delay, then reads until two readings 1 s apart agree.</summary>
    public async Task<CounterSnapshot> ReadStableAsync()
    {
        await Task.Delay(_flushIdle);
        CounterSnapshot previous = await ReadAsync();
        for (int reading = 0; reading < MaximumReadings; reading++)
        {
            await Task.Delay(_agreementGap);
            CounterSnapshot current = await ReadAsync();
            // Agreement is judged on the delayed gate counters only: statement statistics and WAL
            // can keep moving with the host's background services, and the idle window nets that out.
            if (
                TempTableGateKeys.All(key =>
                    current.Values.GetValueOrDefault(key) == previous.Values.GetValueOrDefault(key)
                )
            )
            {
                return current;
            }

            previous = current;
        }

        throw new InvalidOperationException(
            $"Counters did not settle after {MaximumReadings} readings; the server is not idle."
        );
    }

    public Task<CounterSnapshot> ReadAsync() =>
        _provider == PerfProvider.Postgresql ? ReadPostgresqlAsync() : ReadMssqlAsync();

    private async Task<CounterSnapshot> ReadPostgresqlAsync()
    {
        SortedDictionary<string, double> values = new(StringComparer.Ordinal);
        JsonObject detail = [];

        NpgsqlConnectionStringBuilder measured = new(_measuredConnectionString) { Pooling = false };
        await using (NpgsqlConnection connection = new(measured.ConnectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, $"{Marker} SELECT pg_stat_clear_snapshot();");
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = $"""
                {Marker}
                SELECT relname, n_tup_ins, n_tup_del, n_dead_tup
                FROM pg_stat_sys_tables
                WHERE schemaname = 'pg_catalog' AND relname = ANY(@tables)
                ORDER BY relname;
                """;
            command.Parameters.AddWithValue("tables", CatalogTables.ToArray());
            JsonObject dead = [];
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    string table = reader.GetString(0);
                    values[$"catalog.{table}.n_tup_ins"] = reader.GetInt64(1);
                    values[$"catalog.{table}.n_tup_del"] = reader.GetInt64(2);
                    dead[table] = reader.GetInt64(3);
                }
            }

            detail["n_dead_tup (informational)"] = dead;
        }

        await using (NpgsqlConnection server = new(_serverConnectionString))
        {
            await server.OpenAsync();
            await ExecuteAsync(server, $"{Marker} SELECT pg_stat_clear_snapshot();");
            await using (NpgsqlCommand wal = server.CreateCommand())
            {
                wal.CommandText = $"""
                    {Marker}
                    SELECT wal_records, wal_fpi, wal_bytes::float8,
                           pg_wal_lsn_diff(pg_current_wal_insert_lsn(), '0/0')::float8
                    FROM pg_stat_wal;
                    """;
                await using NpgsqlDataReader reader = await wal.ExecuteReaderAsync();
                await reader.ReadAsync();
                values["wal.records"] = reader.GetInt64(0);
                values["wal.fpi"] = reader.GetInt64(1);
                values["wal.bytes"] = reader.GetDouble(2);
                values["wal.insert_lsn_bytes"] = reader.GetDouble(3);
            }

            await using NpgsqlCommand statements = server.CreateCommand();
            statements.CommandText = $"""
                {Marker}
                SELECT s.queryid, s.calls, s.plans, s.total_plan_time, s.total_exec_time, s.wal_bytes::float8,
                       left(s.query, 300)
                FROM pg_stat_statements s
                JOIN pg_database d ON d.oid = s.dbid
                WHERE d.datname = @database AND s.query NOT LIKE '%dms1521-counters%'
                ORDER BY s.calls DESC;
                """;
            statements.Parameters.AddWithValue("database", _databaseName);
            double calls = 0;
            double plans = 0;
            double planTime = 0;
            double execTime = 0;
            double walBytes = 0;
            JsonArray rows = [];
            await using NpgsqlDataReader statementReader = await statements.ExecuteReaderAsync();
            while (await statementReader.ReadAsync())
            {
                calls += statementReader.GetInt64(1);
                plans += statementReader.GetInt64(2);
                planTime += statementReader.GetDouble(3);
                execTime += statementReader.GetDouble(4);
                walBytes += statementReader.GetDouble(5);
                if (rows.Count < 60)
                {
                    rows.Add(
                        new JsonObject
                        {
                            ["queryid"] = statementReader.GetInt64(0),
                            ["calls"] = statementReader.GetInt64(1),
                            ["plans"] = statementReader.GetInt64(2),
                            ["totalPlanMs"] = statementReader.GetDouble(3),
                            ["totalExecMs"] = statementReader.GetDouble(4),
                            ["query"] = statementReader.GetString(6),
                        }
                    );
                }
            }

            values["statements.calls"] = calls;
            values["statements.plans"] = plans;
            values["statements.total_plan_ms"] = Math.Round(planTime, 3);
            values["statements.total_exec_ms"] = Math.Round(execTime, 3);
            values["statements.wal_bytes"] = walBytes;
            detail["pg_stat_statements (top 60 by calls)"] = rows;
        }

        return new CounterSnapshot(DateTime.UtcNow, values, detail);
    }

    private async Task<CounterSnapshot> ReadMssqlAsync()
    {
        SortedDictionary<string, double> values = new(StringComparer.Ordinal);
        JsonObject detail = [];
        await using SqlConnection server = new(_serverConnectionString);
        await server.OpenAsync();

        await using (SqlCommand counters = server.CreateCommand())
        {
            counters.CommandText = """
                SELECT RTRIM(counter_name), cntr_value
                FROM sys.dm_os_performance_counters
                WHERE (object_name LIKE '%General Statistics%'
                       AND counter_name IN ('Temp Tables Creation Rate', 'Active Temp Tables', 'Temp Tables For Destruction'))
                   OR (object_name LIKE '%SQL Statistics%'
                       AND counter_name IN ('SQL Compilations/sec', 'SQL Re-Compilations/sec', 'Batch Requests/sec'));
                """;
            JsonObject current = [];
            await using SqlDataReader reader = await counters.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string name = reader.GetString(0);
                long value = reader.GetInt64(1);
                if (name is "Active Temp Tables" or "Temp Tables For Destruction")
                {
                    current[name] = value;
                }
                else
                {
                    values[$"perf.{name}"] = value;
                }
            }

            detail["current (not cumulative)"] = current;
        }

        await using (SqlCommand waits = server.CreateCommand())
        {
            waits.CommandText = """
                SELECT wait_type, waiting_tasks_count, wait_time_ms
                FROM sys.dm_os_wait_stats
                WHERE wait_type LIKE 'PAGELATCH%';
                """;
            await using SqlDataReader reader = await waits.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                values[$"wait.{reader.GetString(0)}.tasks"] = reader.GetInt64(1);
                values[$"wait.{reader.GetString(0)}.ms"] = reader.GetInt64(2);
            }
        }

        await using (SqlCommand stats = server.CreateCommand())
        {
            stats.CommandText = """
                SELECT qs.execution_count, qs.plan_generation_num, qs.total_logical_reads,
                       qs.total_worker_time, CONVERT(nvarchar(32), qs.creation_time, 126),
                       LEFT(SUBSTRING(st.text, qs.statement_start_offset / 2 + 1,
                           (CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                                 ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2 + 1), 200)
                FROM sys.dm_exec_query_stats qs
                CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
                CROSS APPLY sys.dm_exec_plan_attributes(qs.plan_handle) pa
                WHERE pa.attribute = 'dbid' AND CAST(pa.value AS int) = DB_ID(@database)
                ORDER BY qs.execution_count DESC;
                """;
            stats.Parameters.Add(
                new SqlParameter("@database", System.Data.SqlDbType.NVarChar, 128) { Value = _databaseName }
            );
            double executions = 0;
            double generations = 0;
            double reads = 0;
            int statements = 0;
            JsonArray rows = [];
            await using SqlDataReader reader = await stats.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                statements++;
                executions += reader.GetInt64(0);
                generations += reader.GetInt64(1);
                reads += reader.GetInt64(2);
                if (rows.Count < 60)
                {
                    rows.Add(
                        new JsonObject
                        {
                            ["executions"] = reader.GetInt64(0),
                            ["planGeneration"] = reader.GetInt64(1),
                            ["logicalReads"] = reader.GetInt64(2),
                            ["workerTimeUs"] = reader.GetInt64(3),
                            ["created"] = reader.GetString(4),
                            ["statement"] = reader.IsDBNull(5) ? null : reader.GetString(5),
                        }
                    );
                }
            }

            values["querystats.executions"] = executions;
            values["querystats.plan_generation_sum"] = generations;
            values["querystats.logical_reads"] = reads;
            values["querystats.cached_statements"] = statements;
            detail["dm_exec_query_stats (top 60 by executions)"] = rows;
        }

        await using (SqlCommand tempdb = server.CreateCommand())
        {
            tempdb.CommandText = """
                SELECT SUM(user_object_reserved_page_count), SUM(internal_object_reserved_page_count),
                       SUM(version_store_reserved_page_count)
                FROM tempdb.sys.dm_db_file_space_usage;
                """;
            await using SqlDataReader reader = await tempdb.ExecuteReaderAsync();
            await reader.ReadAsync();
            detail["tempdb reserved pages (current)"] = new JsonObject
            {
                ["userObjects"] = reader.GetInt64(0),
                ["internalObjects"] = reader.GetInt64(1),
                ["versionStore"] = reader.GetInt64(2),
            };
        }

        if (_xeSessionCreated)
        {
            await using SqlCommand histogram = server.CreateCommand();
            histogram.CommandText = $"""
                SELECT CAST(t.target_data AS nvarchar(max))
                FROM sys.dm_xe_session_targets t
                JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address
                WHERE s.name = N'{XeSessionName}' AND t.target_name = N'histogram';
                """;
            string? xml = await histogram.ExecuteScalarAsync() as string;
            double total = 0;
            if (xml is not null)
            {
                foreach (XElement slot in XDocument.Parse(xml).Descendants("Slot"))
                {
                    double count = double.Parse(slot.Attribute("count")!.Value, CultureInfo.InvariantCulture);
                    values[$"recompile.cause.{slot.Element("value")!.Value}"] = count;
                    total += count;
                }
            }

            values["recompile.total"] = total;

            await using SqlCommand eventCounter = server.CreateCommand();
            eventCounter.CommandText = $"""
                SELECT CAST(t.target_data AS nvarchar(max))
                FROM sys.dm_xe_session_targets t
                JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address
                WHERE s.name = N'{XeSessionName}' AND t.target_name = N'event_counter';
                """;
            string? counterXml = await eventCounter.ExecuteScalarAsync() as string;
            values["xe.temp_tables_created"] = counterXml is null
                ? 0
                : XDocument
                    .Parse(counterXml)
                    .Descendants("Event")
                    .Where(element => element.Attribute("name")?.Value == "object_created")
                    .Sum(element =>
                        double.Parse(element.Attribute("count")!.Value, CultureInfo.InvariantCulture)
                    );
            detail["recompile cause map"] = await RecompileCauseMapAsync(server);
        }

        return new CounterSnapshot(DateTime.UtcNow, values, detail);
    }

    private static async Task<JsonObject> RecompileCauseMapAsync(SqlConnection server)
    {
        JsonObject map = [];
        await using SqlCommand command = server.CreateCommand();
        command.CommandText = """
            SELECT map_key, map_value FROM sys.dm_xe_map_values WHERE name = N'statement_recompile_cause';
            """;
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            map[reader.GetInt32(0).ToString(CultureInfo.InvariantCulture)] = reader.GetString(1);
        }

        return map;
    }

    public static SortedDictionary<string, double> Subtract(
        IReadOnlyDictionary<string, double> after,
        IReadOnlyDictionary<string, double> before
    )
    {
        SortedDictionary<string, double> delta = new(StringComparer.Ordinal);
        foreach (string key in after.Keys.Union(before.Keys))
        {
            delta[key] = after.GetValueOrDefault(key) - before.GetValueOrDefault(key);
        }

        return delta;
    }

    public static JsonObject ToJson(IReadOnlyDictionary<string, double> values)
    {
        JsonObject json = [];
        foreach ((string key, double value) in values)
        {
            json[key] = double.IsNaN(value) ? null : value;
        }

        return json;
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}

/// <summary>
/// Proves the observer before any of its zeros gates anything: N keyset-shaped temp-table cycles
/// must register in proportion to N, and plain SELECTs must register zero.
/// </summary>
public static class ObserverCalibration
{
    public const int SmallCycles = 10;
    public const int LargeCycles = 20;

    public static async Task<JsonObject> RunAsync(ServerCounters counters, ReplayTarget target)
    {
        PerfProvider provider = target.Provider;
        string cycleSql =
            provider == PerfProvider.Postgresql
                ? """
                    DROP TABLE IF EXISTS calib_page;
                    CREATE TEMP TABLE calib_page ("DocumentId" bigint PRIMARY KEY, "Ordinal" int NULL) ON COMMIT DROP;
                    INSERT INTO calib_page ("DocumentId") SELECT @id;
                    SELECT "DocumentId" FROM calib_page;
                    """
                : """
                    IF OBJECT_ID('tempdb..#calib_page') IS NOT NULL
                        DROP TABLE #calib_page;
                    CREATE TABLE #calib_page ([DocumentId] bigint PRIMARY KEY, [Ordinal] int NULL);
                    INSERT INTO #calib_page ([DocumentId]) SELECT @id;
                    SELECT [DocumentId] FROM #calib_page;
                    """;
        const string selectSql = "SELECT @id AS calibration_value;";

        async Task<int> RunCyclesAsync(string sql, int count)
        {
            for (int cycle = 0; cycle < count; cycle++)
            {
                await using DbConnection connection = await target.OpenAsync();
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = sql;
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = "@id";
                parameter.Value = (long)cycle + 1;
                parameter.DbType = System.Data.DbType.Int64;
                command.Parameters.Add(parameter);
                await using DbDataReader reader = await command.ExecuteReaderAsync();
                do
                {
                    while (await reader.ReadAsync()) { }
                } while (await reader.NextResultAsync());
            }

            return count;
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        CounterWindow small = await counters.MeasureAsync(() => RunCyclesAsync(cycleSql, SmallCycles));
        CounterWindow large = await counters.MeasureAsync(() => RunCyclesAsync(cycleSql, LargeCycles));
        CounterWindow selects = await counters.MeasureAsync(() => RunCyclesAsync(selectSql, LargeCycles));

        JsonArray checks = [];
        bool calibrated = true;
        double smallGate = 0;
        double largeGate = 0;
        foreach (string key in counters.TempTableGateKeys)
        {
            double smallNet = small.Net.GetValueOrDefault(key);
            double largeNet = large.Net.GetValueOrDefault(key);
            double selectNet = selects.Net.GetValueOrDefault(key);
            smallGate += smallNet;
            largeGate += largeNet;
            bool zeroForSelects = selectNet == 0;
            calibrated &= zeroForSelects;
            checks.Add(
                new JsonObject
                {
                    ["counter"] = key,
                    [$"net for {SmallCycles} cycles"] = smallNet,
                    [$"net for {LargeCycles} cycles"] = largeNet,
                    [$"net for {LargeCycles} plain SELECTs"] = selectNet,
                    ["zero for SELECTs"] = zeroForSelects,
                }
            );
        }

        double ratio = smallGate == 0 ? double.NaN : largeGate / smallGate;
        bool proportional = smallGate > 0 && ratio is >= 1.8 and <= 2.2;
        calibrated &= proportional;
        return new JsonObject
        {
            ["provider"] = PerfProviders.ArtifactName(provider),
            ["calibrated"] = calibrated,
            ["gateCounters"] = new JsonArray([.. counters.TempTableGateKeys.Select(key => (JsonNode)key)]),
            ["gateSum"] = new JsonObject
            {
                [$"{SmallCycles} cycles"] = smallGate,
                [$"{LargeCycles} cycles"] = largeGate,
                ["ratio"] = double.IsNaN(ratio) ? null : ratio,
                ["proportional (1.8 to 2.2)"] = proportional,
            },
            ["checks"] = checks,
            ["windows"] = new JsonObject
            {
                ["cycles.small"] = small.ToJson(),
                ["cycles.large"] = large.ToJson(),
                ["selects"] = selects.ToJson(),
            },
            ["elapsedSeconds"] = elapsed.Elapsed.TotalSeconds,
        };
    }
}
