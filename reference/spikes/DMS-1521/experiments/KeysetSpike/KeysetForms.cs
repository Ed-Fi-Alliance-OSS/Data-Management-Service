// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;

namespace KeysetSpike;

/// <summary>
/// Task 8 screening on SQL Server: derived keyset forms of the captured task 6 batches, replayed on a
/// loaded <c>primary-500k</c> lease. Screening only: these are text transforms, so they never exercise
/// production binding, readers, anchors, or retries.
/// </summary>
[TestFixture]
[Explicit("DMS-1521 evidence run; started by Invoke-KeysetSpike.ps1")]
[Category("DMS1521")]
public class Given_Mssql_KeysetFormsScreening : MssqlApiIntegrationTestBase
{
    private string _leased = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override bool MatchProductionWriteIsolation => true;

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture) =>
        _leased = await base.LeaseDatabaseAsync(fixture);

    [Test]
    public async Task It_screens_the_keyset_forms() =>
        await TestContext.Out.WriteLineAsync(
            "Run directory: "
                + await KeysetForms.RunAsync(
                    new(Harness, PerfProvider.Mssql, _leased, () => OpenAssertionConnectionAsync(_leased))
                )
        );
}

/// <summary>
/// The SQL Server keyset forms, each derived from the captured <c>baseline</c> batch:
/// <list type="bullet">
/// <item><c>baseline</c> and <c>inline</c>: the task 6 captures, unchanged.</item>
/// <item><c>tablevar</c>: <c>#page</c> becomes a table variable with the same columns and key.</item>
/// <item><c>values</c>: every keyset join reads a parameterized <c>VALUES</c> list.</item>
/// <item><c>tvp</c>: every keyset join reads a table-valued parameter of a type the run creates.</item>
/// </list>
/// A selected page already holds its ids in a parameter, so <c>values</c> and <c>tvp</c> run in one
/// round trip. A query keyset selects its page in the batch, so for those two forms it runs in two
/// phases on one connection: the selection returns the ids, and the hydration binds them.
/// </summary>
public static class KeysetForms
{
    public static readonly IReadOnlyList<string> AllForms =
    [
        "baseline",
        "inline",
        "tablevar",
        "values",
        "tvp",
        "memtablevar",
    ];

    public const string TvpTypeName = "dbo.dms1521_PageKeyset";

    public const string MemoryOptimizedTypeName = "dbo.dms1521_PageKeysetMem";

    private const string Prelude =
        "IF OBJECT_ID('tempdb..[#page]') IS NOT NULL\n    DROP TABLE [#page];\n"
        + "CREATE TABLE [#page] ([DocumentId] bigint PRIMARY KEY, [Ordinal] int NULL);\n";

    private const string KeysetJoin = "INNER JOIN [#page] k ON";

    private const string MemoryOptimizedProvisioningSql = $"""
        IF NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE type = 'FX')
        BEGIN
            DECLARE @database sysname = DB_NAME();
            DECLARE @path nvarchar(4000) =
                CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000)) + @database + N'_dms1521_mod';
            DECLARE @addFilegroup nvarchar(max) =
                N'ALTER DATABASE ' + QUOTENAME(@database) + N' ADD FILEGROUP [dms1521_mod] CONTAINS MEMORY_OPTIMIZED_DATA;';
            DECLARE @addFile nvarchar(max) =
                N'ALTER DATABASE ' + QUOTENAME(@database) + N' ADD FILE (NAME = N''dms1521_mod'', FILENAME = N'''
                + @path + N''') TO FILEGROUP [dms1521_mod];';
            EXEC (@addFilegroup);
            EXEC (@addFile);
        END;
        IF TYPE_ID(N'{MemoryOptimizedTypeName}') IS NULL
            EXEC (N'CREATE TYPE {MemoryOptimizedTypeName} AS TABLE ([DocumentId] bigint NOT NULL PRIMARY KEY NONCLUSTERED, [Ordinal] int NULL) WITH (MEMORY_OPTIMIZED = ON);');
        """;

    private const string QueryMaterialization =
        "INSERT INTO [#page] ([DocumentId])\nOUTPUT INSERTED.[DocumentId]\nSELECT";

    public static async Task<string> RunAsync(KeysetSpikeContext context)
    {
        KeysetSpikeSettings settings = KeysetSpikeSettings.FromEnvironment();
        RunArtifacts run = RunArtifacts.Create(settings, context.Provider, "forms");
        KeysetSpikeRunner.Guard(settings, run);
        string source = Environment.GetEnvironmentVariable("DMS1521_FORMS_SOURCE") is { Length: > 0 } value
            ? value
            : "t6s1";
        int rounds = int.Parse(
            Environment.GetEnvironmentVariable("DMS1521_FORMS_ROUNDS") is { Length: > 0 } text ? text : "3",
            CultureInfo.InvariantCulture
        );
        // The first screening (t8) ran the five forms; memtablevar is screened only after they all fail.
        IReadOnlyList<string> forms = Environment.GetEnvironmentVariable("DMS1521_FORMS")
            is { Length: > 0 } list
            ? [.. list.Split(',').Select(form => form.Trim())]
            : [.. AllForms.Take(5)];
        if (forms[0] != "baseline" || forms.Any(form => !AllForms.Contains(form)))
        {
            throw new InvalidOperationException(
                $"DMS1521_FORMS must start with baseline and name known forms: {string.Join(',', forms)}."
            );
        }

        run.Manifest["formsSource"] = source;
        run.Manifest["formsRounds"] = rounds;
        run.Manifest["forms"] = new JsonArray([.. forms.Select(form => (JsonNode)form)]);

        await using ReplayTarget target = new(context.Provider, context.LeasedConnectionString);
        await using ServerCounters counters = await KeysetSpikeRunner.StartCountersAsync(settings, context);
        if (settings.Calibrate)
        {
            JsonObject calibration = await ObserverCalibration.RunAsync(counters, target);
            run.WriteJson("calibration.json", calibration);
            ObserverCalibration.RequireCalibrated(calibration);
            // Informational: a table variable registers only in the server-wide counter, not proportionally.
            run.WriteJson("calibration-tablevar.json", await CalibrateTableVariablesAsync(counters, target));
        }

        Stopwatch load = Stopwatch.StartNew();
        await PerfFixtureLoader.LoadAndVerifyAsync(
            context.Harness.DbConnection,
            context.Provider,
            new PerfFixtureDefinition(settings.Fixture)
        );
        run.Manifest["fixtureLoadSeconds"] = load.Elapsed.TotalSeconds;

        await using (DbConnection connection = await context.OpenConnectionAsync())
        {
            if (forms.Contains("tvp"))
            {
                await ExecuteNonQueryAsync(
                    connection,
                    $"IF TYPE_ID(N'{TvpTypeName}') IS NULL CREATE TYPE {TvpTypeName} AS TABLE ([DocumentId] bigint PRIMARY KEY, [Ordinal] int NULL);"
                );
            }

            if (forms.Contains("memtablevar"))
            {
                // The provisioning a memory-optimized table variable needs: a MEMORY_OPTIMIZED_DATA
                // filegroup (SQL Server 2022 and older can never remove it; SQL Server 2025 can, after the
                // type is dropped, through the In-Memory OLTP undeploy procedure) and a memory-optimized type.
                Stopwatch provisioning = Stopwatch.StartNew();
                await ExecuteNonQueryAsync(connection, MemoryOptimizedProvisioningSql);
                run.Manifest["memoryOptimizedProvisioningSeconds"] = provisioning.Elapsed.TotalSeconds;
                run.Manifest["memoryOptimizedProvisioningSql"] = MemoryOptimizedProvisioningSql;
            }
        }

        // (kind, page size) -> form -> batch
        List<(string Kind, int PageSize, Dictionary<string, ReplayBatch> ByForm)> cases = [];
        foreach (
            (string fixtureRun, string kind) in new[]
            {
                ("resource", "resource.traditional-offset-zero"),
                ("read-acceleration", "fallback"),
            }
        )
        {
            foreach (int pageSize in settings.PageSizes)
            {
                ReplayBatch baseline = LoadCaptured(settings, source, fixtureRun, "B1", kind, pageSize);
                ReplayBatch inline = LoadCaptured(settings, source, fixtureRun, "I2", kind, pageSize);
                cases.Add(
                    (kind, pageSize, forms.ToDictionary(form => form, form => Build(form, baseline, inline)))
                );
            }
        }

        JsonArray results = [];
        foreach ((string kind, int pageSize, Dictionary<string, ReplayBatch> byForm) in cases)
        {
            IReadOnlyList<string> expected = await ResultFingerprintAsync(target, byForm["baseline"]);
            if (
                !expected.Any(fingerprint => fingerprint.StartsWith($"{pageSize}:", StringComparison.Ordinal))
            )
            {
                throw new InvalidOperationException(
                    $"{kind}.{pageSize}: the baseline returned no {pageSize}-row result set; the captured ids do not match this lease."
                );
            }

            foreach (string form in forms)
            {
                ReplayBatch batch = byForm[form];
                IReadOnlyList<string> actual = await ResultFingerprintAsync(target, batch);
                results.Add(
                    new JsonObject
                    {
                        ["kind"] = kind,
                        ["pageSize"] = pageSize,
                        ["form"] = form,
                        ["phases"] = batch.SecondPhase is null ? 1 : 2,
                        ["sqlLength"] = SqlLength(batch),
                        ["parameterCount"] = ParameterCount(batch),
                        ["firstExecutionMs"] = await FirstExecutionMsAsync(context, target, batch),
                        ["sameResultSetsAsBaseline"] = actual.SequenceEqual(expected),
                        ["resultSets"] = new JsonArray([
                            .. actual.Select(fingerprint => (JsonNode)fingerprint),
                        ]),
                    }
                );
            }
        }

        run.WriteJson("forms.json", results);

        JsonArray replays = [];
        for (int round = 1; round <= rounds; round++)
        {
            foreach ((string kind, int pageSize, Dictionary<string, ReplayBatch> byForm) in cases)
            {
                // Rotate the form order each round, so no form always runs first or last.
                IEnumerable<string> order = forms
                    .Skip((round - 1) % forms.Count)
                    .Concat(forms.Take((round - 1) % forms.Count));
                foreach (string form in order)
                {
                    foreach (int concurrency in settings.ReplayConcurrency)
                    {
                        CounterWindow? window = null;
                        Func<Func<Task>, Task>? measuredPhaseWindow =
                            round == 1
                            && settings.Counters
                            && settings.CounterConcurrency.Contains(concurrency)
                                ? async measuredPhase =>
                                    window = await counters.MeasureAsync(async () =>
                                    {
                                        await measuredPhase();
                                        return concurrency * settings.ReplayIterationsPerWorker;
                                    })
                                : null;
                        ReplayResult result = await KeysetReplay.RunAsync(
                            target,
                            byForm[form],
                            concurrency,
                            settings.ReplayWarmupPerWorker,
                            settings.ReplayIterationsPerWorker,
                            measuredPhaseWindow
                        );
                        JsonObject entry = new()
                        {
                            ["round"] = round,
                            ["kind"] = kind,
                            ["pageSize"] = pageSize,
                            ["form"] = form,
                            ["replay"] = result.ToJson(),
                        };
                        if (window is not null)
                        {
                            entry["counters"] = window.ToJson();
                        }

                        replays.Add(entry);
                    }
                }
            }

            run.WriteJson("replays.json", replays);
        }

        if (forms.Contains("memtablevar"))
        {
            await using DbConnection connection = await context.OpenConnectionAsync();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT SUM(pages_kb) FROM sys.dm_os_memory_clerks WHERE type LIKE 'MEMORYCLERK_XTP%';";
            run.Manifest["xtpMemoryClerkKbAfterReplays"] = Convert.ToInt64(
                await command.ExecuteScalarAsync(),
                CultureInfo.InvariantCulture
            );
        }

        return await KeysetSpikeRunner.FinishAsync(settings, context, run);
    }

    /// <summary>Builds one form from the captured baseline (or returns a capture unchanged).</summary>
    public static ReplayBatch Build(string form, ReplayBatch baseline, ReplayBatch inline) =>
        form switch
        {
            "baseline" => baseline,
            "inline" => inline,
            "tablevar" => baseline with
            {
                Text =
                    "DECLARE @page TABLE ([DocumentId] bigint PRIMARY KEY, [Ordinal] int NULL);\n"
                    + AfterPrelude(baseline).Replace("[#page]", "@page", StringComparison.Ordinal),
            },
            "memtablevar" => baseline with
            {
                Text =
                    $"DECLARE @page {MemoryOptimizedTypeName};\n"
                    + AfterPrelude(baseline).Replace("[#page]", "@page", StringComparison.Ordinal),
            },
            "values" or "tvp" => ClientHeld(form, baseline),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, "Unknown form."),
        };

    private static string AfterPrelude(ReplayBatch baseline) =>
        baseline.Text.StartsWith(Prelude, StringComparison.Ordinal)
            ? baseline.Text[Prelude.Length..]
            : throw new InvalidOperationException(
                $"{baseline.Kind}: the baseline batch does not start with the #page prelude."
            );

    private static ReplayBatch ClientHeld(string form, ReplayBatch baseline)
    {
        string afterPrelude = AfterPrelude(baseline);
        int end = afterPrelude.IndexOf(";\n", StringComparison.Ordinal) + 2;
        string materialization = afterPrelude[..end];
        string body = afterPrelude[end..];

        if (
            baseline
                .Parameters.FirstOrDefault(parameter => parameter.BareName == "selectedDocumentIdsJson")
                ?.Value
            is string json
        )
        {
            List<(long Id, int? Ordinal)> rows =
            [
                .. JsonNode
                    .Parse(json)!
                    .AsArray()
                    .Select(node =>
                        (node!["DocumentId"]!.GetValue<long>(), (int?)node["Ordinal"]!.GetValue<int>())
                    ),
            ];
            return Hydration(form, baseline, body, rows);
        }

        if (!materialization.Contains(QueryMaterialization, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{baseline.Kind}: unexpected materialization statement.");
        }

        return baseline with
        {
            Text = materialization.Replace(QueryMaterialization, "SELECT", StringComparison.Ordinal),
            SecondPhase = ids => Hydration(form, baseline, body, [.. ids.Select(id => (id, (int?)null))]),
        };
    }

    private static ReplayBatch Hydration(
        string form,
        ReplayBatch baseline,
        string body,
        IReadOnlyList<(long Id, int? Ordinal)> rows
    )
    {
        List<SpikeParameterSpec> parameters = [];
        string relation;
        if (form == "values")
        {
            if (rows.Count == 0)
            {
                relation =
                    "(SELECT CAST(NULL AS bigint) AS [DocumentId], CAST(NULL AS int) AS [Ordinal] WHERE 1 = 0) k";
            }
            else
            {
                StringBuilder values = new("(VALUES ");
                for (int index = 0; index < rows.Count; index++)
                {
                    values
                        .Append(index == 0 ? "" : ", ")
                        .Append(CultureInfo.InvariantCulture, $"(@k{index}, ");
                    parameters.Add(Scalar($"@k{index}", rows[index].Id, SqlDbType.BigInt));
                    if (rows[index].Ordinal is int ordinal)
                    {
                        values.Append(CultureInfo.InvariantCulture, $"@o{index})");
                        parameters.Add(Scalar($"@o{index}", ordinal, SqlDbType.Int));
                    }
                    else
                    {
                        values.Append("CAST(NULL AS int))");
                    }
                }

                relation = values.Append(") k([DocumentId], [Ordinal])").ToString();
            }
        }
        else
        {
            DataTable table = new();
            table.Columns.Add("DocumentId", typeof(long));
            table.Columns.Add("Ordinal", typeof(int));
            foreach ((long id, int? ordinal) in rows)
            {
                table.Rows.Add(id, ordinal is int value ? value : DBNull.Value);
            }

            parameters.Add(
                new SpikeParameterSpec(
                    "@page_keyset",
                    table,
                    DbType.Object,
                    nameof(SqlDbType.Structured),
                    (int)SqlDbType.Structured,
                    null,
                    0,
                    0,
                    0,
                    ParameterDirection.Input,
                    TvpTypeName
                )
            );
            relation = "@page_keyset k";
        }

        return new ReplayBatch(
            baseline.Kind,
            baseline.PageSize,
            body.Replace(KeysetJoin, $"INNER JOIN {relation} ON", StringComparison.Ordinal),
            parameters
        );
    }

    private static SpikeParameterSpec Scalar(string name, object value, SqlDbType type) =>
        new(
            name,
            value,
            type == SqlDbType.BigInt ? DbType.Int64 : DbType.Int32,
            type.ToString(),
            (int)type,
            null,
            0,
            0,
            0,
            ParameterDirection.Input,
            null
        );

    /// <summary>Loads a task 6 capture: its batch text and its SQL Server binding specifications.</summary>
    private static ReplayBatch LoadCaptured(
        KeysetSpikeSettings settings,
        string source,
        string fixtureRun,
        string label,
        string kind,
        int pageSize
    )
    {
        string[] directories = Directory.GetDirectories(
            settings.ResultsDirectory,
            $"{source}-mssql-{fixtureRun}-{label}-*"
        );
        if (directories.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one {source}-mssql-{fixtureRun}-{label} run, found {directories.Length}."
            );
        }

        string prefix = Path.Combine(directories[0], "batches", $"{kind}.{pageSize}");
        string text = File.ReadAllText($"{prefix}.sql");
        JsonObject specification = JsonNode.Parse(File.ReadAllText($"{prefix}.params.json"))!.AsObject();
        if (specification["sha256"]!.GetValue<string>() != KeysetSpikeRunner.Sha256(text))
        {
            throw new InvalidOperationException($"{prefix}.sql does not match its recorded SHA-256.");
        }

        List<SpikeParameterSpec> parameters = [];
        foreach (JsonNode? node in specification["parameters"]!.AsArray())
        {
            string providerType = node!["providerType"]!.GetValue<string>();
            object? value = node["value"] is null
                ? null
                : providerType switch
                {
                    "BigInt" => node["value"]!.GetValue<long>(),
                    "Int" or "SmallInt" => node["value"]!.GetValue<int>(),
                    "NVarChar" => node["value"]!.GetValue<string>(),
                    _ => throw new InvalidOperationException(
                        $"{prefix}: unsupported provider type {providerType}."
                    ),
                };
            parameters.Add(
                new SpikeParameterSpec(
                    node["name"]!.GetValue<string>(),
                    providerType == "SmallInt" && value is int small ? (short)small : value,
                    Enum.Parse<DbType>(node["dbType"]!.GetValue<string>()),
                    providerType,
                    node["providerTypeCode"]!.GetValue<int>(),
                    null,
                    node["size"]!.GetValue<int>(),
                    node["precision"]!.GetValue<byte>(),
                    node["scale"]!.GetValue<byte>(),
                    Enum.Parse<ParameterDirection>(node["direction"]!.GetValue<string>()),
                    node["typeName"]?.GetValue<string>()
                )
            );
        }

        return new ReplayBatch(kind, pageSize, text, parameters);
    }

    /// <summary>
    /// Every result set the form returns (both phases), as "rows:sha256" of its rows sorted, so two
    /// forms can be compared without depending on the order an unordered result set arrives in.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ResultFingerprintAsync(
        ReplayTarget target,
        ReplayBatch batch
    )
    {
        await using DbConnection connection = await target.OpenAsync();
        List<string> fingerprints = [];

        async Task<List<long>> RunAsync(ReplayBatch phase)
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = phase.Text;
            command.CommandTimeout = 600;
            foreach (SpikeParameterSpec parameter in phase.Parameters)
            {
                parameter.Bind(command);
            }

            List<long> firstIds = [];
            bool first = true;
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            do
            {
                List<string> rows = [];
                while (await reader.ReadAsync())
                {
                    rows.Add(
                        string.Join(
                            '|',
                            Enumerable
                                .Range(0, reader.FieldCount)
                                .Select(ordinal =>
                                    reader.IsDBNull(ordinal) ? "<null>"
                                    : reader.GetValue(ordinal) is byte[] bytes ? Convert.ToHexString(bytes)
                                    : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)
                                )
                        )
                    );
                    if (first)
                    {
                        firstIds.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
                    }
                }

                rows.Sort(StringComparer.Ordinal);
                fingerprints.Add($"{rows.Count}:{KeysetSpikeRunner.Sha256(string.Join('\n', rows))}");
                first = false;
            } while (await reader.NextResultAsync());

            return firstIds;
        }

        List<long> ids = await RunAsync(batch);
        if (batch.SecondPhase is not null)
        {
            await RunAsync(batch.SecondPhase(ids));
        }

        return fingerprints;
    }

    /// <summary>One request's latency right after the database's plan cache is cleared (compile included).</summary>
    private static async Task<double> FirstExecutionMsAsync(
        KeysetSpikeContext context,
        ReplayTarget target,
        ReplayBatch batch
    )
    {
        await using (DbConnection admin = await context.OpenConnectionAsync())
        {
            await ExecuteNonQueryAsync(admin, "ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;");
        }

        long start = Stopwatch.GetTimestamp();
        await using DbConnection connection = await target.OpenAsync();
        IReadOnlyList<long> ids = await KeysetReplay.ExecuteAsync(connection, batch);
        if (batch.SecondPhase is not null)
        {
            await KeysetReplay.ExecuteAsync(connection, batch.SecondPhase(ids));
        }

        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>
    /// SQL Server counts each table variable a batch declares in Temp Tables Creation Rate, but no
    /// object_created event fires for it. This records both for 10 and 20 table-variable batches and
    /// for 20 plain batches, so the tablevar form's counters can be read.
    /// </summary>
    private static async Task<JsonObject> CalibrateTableVariablesAsync(
        ServerCounters counters,
        ReplayTarget target
    )
    {
        ReplayBatch tableVariable = new(
            "calibration.tablevar",
            1,
            "DECLARE @page TABLE ([DocumentId] bigint PRIMARY KEY, [Ordinal] int NULL); INSERT INTO @page ([DocumentId]) VALUES (1); SELECT [DocumentId] FROM @page;",
            []
        );
        ReplayBatch plain = new("calibration.plain", 1, "SELECT 1 AS calibration_value;", []);

        async Task<JsonObject> WindowAsync(ReplayBatch batch, int count)
        {
            CounterWindow window = await counters.MeasureAsync(async () =>
            {
                for (int index = 0; index < count; index++)
                {
                    await using DbConnection connection = await target.OpenAsync();
                    await KeysetReplay.ExecuteAsync(connection, batch);
                }

                return count;
            });
            return new JsonObject
            {
                ["batches"] = count,
                ["perf.Temp Tables Creation Rate"] = window.Net.GetValueOrDefault(
                    "perf.Temp Tables Creation Rate"
                ),
                ["xe.temp_tables_created"] = window.Net.GetValueOrDefault("xe.temp_tables_created"),
            };
        }

        return new JsonObject
        {
            ["tablevar.10"] = await WindowAsync(tableVariable, 10),
            ["tablevar.20"] = await WindowAsync(tableVariable, 20),
            ["plain.20"] = await WindowAsync(plain, 20),
        };
    }

    private static int SqlLength(ReplayBatch batch) =>
        batch.Text.Length
        + (batch.SecondPhase is null ? 0 : batch.SecondPhase(Placeholder(batch.PageSize)).Text.Length);

    private static int ParameterCount(ReplayBatch batch) =>
        batch.Parameters.Count
        + (batch.SecondPhase is null ? 0 : batch.SecondPhase(Placeholder(batch.PageSize)).Parameters.Count);

    private static long[] Placeholder(int pageSize) =>
        [.. Enumerable.Range(1, pageSize).Select(id => (long)id)];

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
