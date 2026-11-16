// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace KeysetSpike;

/// <summary>
/// Task 4 PostgreSQL carrier probes for the inline variant, on <c>smoke-10k</c>. Every probe runs the
/// production batch builder, executor, and reader (<see cref="ConsistentHydrationExecutor" />) on one
/// pooled physical connection built as production builds it (NoResetOnClose, auto-prepare), and asserts
/// the exact document ids returned.
/// </summary>
[TestFixture]
[Explicit("DMS-1521 carrier probes; started by Invoke-KeysetSpike.ps1 -Fixtures probes")]
[Category("DMS1521")]
public class Given_Postgresql_KeysetCarrierProbes : PostgresqlApiIntegrationTestBase
{
    private readonly CapturedHydration _captured = new();
    private string _leased = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture) =>
        _leased = await base.LeaseDatabaseAsync(fixture);

    protected override void ConfigureAdditionalServices(IServiceCollection services)
    {
        ServiceDescriptor hydrator = services.Last(service =>
            service.ServiceType == typeof(IDocumentHydrator)
        );
        services.Replace(
            ServiceDescriptor.Scoped<IDocumentHydrator>(provider => new CapturingHydrator(
                (IDocumentHydrator)(
                    hydrator.ImplementationInstance
                    ?? hydrator.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.CreateInstance(provider, hydrator.ImplementationType!)
                ),
                _captured
            ))
        );
    }

    [Test]
    public async Task It_runs_the_carrier_probes() =>
        await TestContext.Out.WriteLineAsync(
            "Run directory: "
                + await CarrierProbes.RunResourceAsync(
                    new(
                        Harness,
                        PerfProvider.Postgresql,
                        _leased,
                        () => OpenAssertionConnectionAsync(_leased)
                    ),
                    _captured
                )
        );
}

/// <summary>
/// Task 4 PostgreSQL carrier probes for the descriptor batch, whose statements read the carrier with no
/// bound parameter (carrier-only), on <c>descriptors-smoke-2k</c>.
/// </summary>
[TestFixture]
[Explicit("DMS-1521 carrier probes; started by Invoke-KeysetSpike.ps1 -Fixtures descriptorprobes")]
[Category("DMS1521")]
public class Given_Postgresql_KeysetCarrierDescriptorProbes : PostgresqlApiIntegrationTestBase
{
    private string _leased = null!;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture) =>
        _leased = await base.LeaseDatabaseAsync(fixture);

    [Test]
    public async Task It_runs_the_descriptor_carrier_probes() =>
        await TestContext.Out.WriteLineAsync(
            "Run directory: "
                + await CarrierProbes.RunDescriptorAsync(
                    new(
                        Harness,
                        PerfProvider.Postgresql,
                        _leased,
                        () => OpenAssertionConnectionAsync(_leased)
                    )
                )
        );
}

/// <summary>The last hydration request a GET made: its plan, keyset, and options.</summary>
public sealed class CapturedHydration
{
    public ResourceReadPlan? Plan { get; set; }

    public PageKeysetSpec? Keyset { get; set; }

    public HydrationExecutionOptions? Options { get; set; }
}

internal sealed class CapturingHydrator(IDocumentHydrator inner, CapturedHydration captured)
    : IDocumentHydrator
{
    public Task<HydratedPage> HydrateAsync(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        HydrationExecutionOptions executionOptions,
        CancellationToken ct
    )
    {
        captured.Plan = plan;
        captured.Keyset = keyset;
        captured.Options = executionOptions;
        return inner.HydrateAsync(plan, keyset, executionOptions, ct);
    }
}

public static class CarrierProbes
{
    // The prototype's names, repeated here so the runner also builds on pristine main (variant B0).
    private const string CarrierSetting = "dms.page_keyset";

    private static bool IsInline =>
        Environment.GetEnvironmentVariable("DMS1521_KEYSET_VARIANT")?.Trim() == "inline";

    private static readonly string[] PlanCacheModes = ["auto", "force_custom_plan", "force_generic_plan"];
    private const int Executions = 8;

    public static async Task<string> RunResourceAsync(KeysetSpikeContext context, CapturedHydration captured)
    {
        KeysetSpikeSettings settings = KeysetSpikeSettings.FromEnvironment();
        RunArtifacts run = RunArtifacts.Create(settings, context.Provider, "carrier-probes");
        KeysetSpikeRunner.Guard(settings, run);
        Require(IsInline, "carrier probes need DMS1521_KEYSET_VARIANT=inline");
        await PerfFixtureLoader.LoadAndVerifyAsync(
            context.Harness.DbConnection,
            context.Provider,
            new PerfFixtureDefinition(PerfFixtureKind.Smoke10k)
        );

        // One real GET supplies the plan, the query keyset, and the production batch with its bindings.
        using SpikeCommandCapture capture = SpikeCommandCapture.Start(context.Provider);
        int commandBaseline = capture.Count;
        int keysetBaseline = context.Recorder.HydrationKeysets.Count;
        await GetAsync(context, $"{PerfFixtureDefinition.ResourceEndpoint}?limit=25&offset=0");
        capture.AttachProductionBindings(
            commandBaseline,
            [.. context.Recorder.HydrationKeysets.Skip(keysetBaseline)],
            []
        );
        SpikeCapturedCommand batch = capture
            .CommandsSince(commandBaseline)
            .Last(command => command.Parameters is not null && command.CommandText.Contains("set_config"));
        ResourceReadPlan plan =
            captured.Plan ?? throw new InvalidOperationException("No hydration captured.");
        HydrationExecutionOptions options = captured.Options!;
        PageKeysetSpec.Query query = (PageKeysetSpec.Query)captured.Keyset!;

        long[] all = await AllStudentIdsAsync(context.Harness.DbConnection);
        Probe probe = new(plan, options, query, context.LeasedConnectionString);
        JsonArray results = [];

        // 1. Sizes, empty and zero-size pages, and selected pages, in sequence on one physical connection.
        long[] shuffled = [.. all.Where((_, index) => index % 7 == 3).Reverse()];
        (string Name, PageKeysetSpec Keyset, long[] Expected)[] sequence =
        [
            ("query 25 @0", probe.Query(25, 0), all[..25]),
            ("query 25 past the end (empty selection)", probe.Query(25, 1_000_000), []),
            ("query zero-size", probe.Query(0, 0), []),
            ("query 1 @0", probe.Query(1, 0), all[..1]),
            ("query 25 @100", probe.Query(25, 100), all[100..125]),
            ("query 500 @0", probe.Query(500, 0), all[..500]),
            ("query 500 @9000", probe.Query(500, 9000), all[9000..9500]),
            ("selected 25 (descending order)", Selected(shuffled[..25]), shuffled[..25]),
            ("selected empty", Selected([]), []),
            ("selected 1", Selected(shuffled[..1]), shuffled[..1]),
            ("selected 500 (descending order)", Selected(shuffled[..500]), shuffled[..500]),
            ("query 25 past the end after selected", probe.Query(25, 1_000_000), []),
            ("query 25 @0 again", probe.Query(25, 0), all[..25]),
        ];
        await using (NpgsqlDataSource pool = probe.Pool("sequence"))
        {
            int? backend = null;
            foreach ((string name, PageKeysetSpec keyset, long[] expected) in sequence)
            {
                ProbeResult result = await probe.HydrateAsync(pool, keyset);
                backend ??= result.BackendPid;
                results.Add(Check("sequence", name, expected, result, backend.Value));
            }
        }

        // 2. A batch that fails after the carrier is set leaks nothing to the next batch.
        await using (NpgsqlDataSource pool = probe.Pool("failure"))
        {
            string? failure = null;
            int pid;
            await using (NpgsqlConnection connection = await pool.OpenConnectionAsync())
            {
                pid = connection.ProcessID;
                await using NpgsqlCommand command = connection.CreateCommand();
                command.CommandText = batch.CommandText + "\nSELECT 1 / 0;";
                foreach (SpikeParameterSpec parameter in batch.Parameters!)
                {
                    parameter.Bind(command);
                }

                try
                {
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
                    do
                    {
                        while (await reader.ReadAsync()) { }
                    } while (await reader.NextResultAsync());
                }
                catch (PostgresException exception)
                {
                    failure = exception.SqlState;
                }
            }

            string? carrierAfterFailure = await probe.CarrierAsync(pool);
            results.Add(
                new JsonObject
                {
                    ["group"] = "failure",
                    ["probe"] = "batch fails after the carrier is set (22012), then the carrier is read",
                    ["sqlState"] = failure,
                    ["carrierAfter"] = carrierAfterFailure,
                    ["passed"] = failure == "22012" && string.IsNullOrEmpty(carrierAfterFailure),
                }
            );
            foreach (
                (string name, PageKeysetSpec keyset) in new (string, PageKeysetSpec)[]
                {
                    ("query empty after failure", probe.Query(25, 1_000_000)),
                    ("selected empty after failure", Selected([])),
                }
            )
            {
                results.Add(Check("failure", name, [], await probe.HydrateAsync(pool, keyset), pid));
            }
        }

        // 3. A retried ConsistentHydrationExecutor attempt: the first attempt bumps a page document's
        //    ContentVersion between the leading and trailing version fences, so the fences disagree and
        //    the executor runs the batch again on the same connection.
        await using (NpgsqlDataSource pool = probe.Pool("retry"))
        {
            ProbeResult result = await probe.HydrateAsync(
                pool,
                probe.Query(25, 0),
                (sql, attempt) =>
                {
                    if (attempt > 1)
                    {
                        return sql;
                    }

                    int trailing = sql.LastIndexOf(
                        "SELECT d.\"DocumentId\", d.\"ContentVersion\" FROM",
                        StringComparison.Ordinal
                    );
                    Require(trailing > 0, "trailing version fence not found");
                    return sql[..trailing]
                        + $"UPDATE \"dms\".\"Document\" SET \"ContentVersion\" = \"ContentVersion\" + 1 WHERE \"DocumentId\" = {all[0]};\n"
                        + sql[trailing..];
                }
            );
            JsonObject entry = Check(
                "retry",
                "query 25 @0 with a forced retry",
                all[..25],
                result,
                result.BackendPid
            );
            entry["attempts"] = result.Attempts;
            entry["passed"] = entry["passed"]!.GetValue<bool>() && result.Attempts == 2;
            results.Add(entry);
        }

        // 4. More than five executions under each plan_cache_mode, with the plans actually used.
        JsonArray prepared = [];
        foreach (string mode in PlanCacheModes)
        {
            await using NpgsqlDataSource pool = probe.Pool($"plan-cache-{mode}");
            await probe.ExecuteAsync(pool, $"SET plan_cache_mode = '{mode}';");
            for (int execution = 1; execution <= Executions; execution++)
            {
                async Task RunPairAsync()
                {
                    results.Add(
                        Check(
                            $"plan-cache {mode}",
                            $"query 25 @0 #{execution}",
                            all[..25],
                            await probe.HydrateAsync(pool, probe.Query(25, 0)),
                            null
                        )
                    );
                    results.Add(
                        Check(
                            $"plan-cache {mode}",
                            $"selected 25 #{execution}",
                            shuffled[..25],
                            await probe.HydrateAsync(pool, Selected(shuffled[..25])),
                            null
                        )
                    );
                }

                if (execution == Executions && AutoExplainWindow.Enabled)
                {
                    await AutoExplainWindow.CaptureAsync(
                        context.OpenConnectionAsync,
                        run,
                        $"probe-{mode}",
                        RunPairAsync
                    );
                }
                else
                {
                    await RunPairAsync();
                }
            }

            prepared.Add(
                new JsonObject { ["mode"] = mode, ["statements"] = await probe.PreparedStatementsAsync(pool) }
            );
        }

        return Finish(run, results, prepared);
    }

    public static async Task<string> RunDescriptorAsync(KeysetSpikeContext context)
    {
        KeysetSpikeSettings settings = KeysetSpikeSettings.FromEnvironment();
        RunArtifacts run = RunArtifacts.Create(settings, context.Provider, "carrier-descriptor-probes");
        KeysetSpikeRunner.Guard(settings, run);
        Require(IsInline, "carrier probes need DMS1521_KEYSET_VARIANT=inline");
        await PerfDescriptorFixtureLoader.LoadAndVerifyAsync(
            context.Harness.DbConnection,
            context.Provider,
            new PerfDescriptorFixtureDefinition(PerfDescriptorFixtureKind.DescriptorsSmoke2k)
        );

        using SpikeCommandCapture capture = SpikeCommandCapture.Start(context.Provider);
        int commandBaseline = capture.Count;
        int relationalBaseline = context.Recorder.RelationalCommands.Count;
        await GetAsync(context, $"{PerfDescriptorFixtureDefinition.ResourceEndpoint}?limit=25&offset=0");
        capture.AttachProductionBindings(
            commandBaseline,
            [],
            [.. context.Recorder.RelationalCommands.Skip(relationalBaseline)]
        );
        SpikeCapturedCommand batch = capture
            .CommandsSince(commandBaseline)
            .Last(command =>
                command.Parameters is not null
                && command.CommandText.Contains("-- Descriptor acquisition:", StringComparison.Ordinal)
            );

        long resourceKeyId = KeysetSpikeRunner.BoundLong(batch.Parameters!, "resourceKeyId")!.Value;
        long[] expected = await ScalarListAsync(
            context.Harness.DbConnection,
            $"SELECT \"DocumentId\" FROM \"dms\".\"Descriptor\" WHERE \"ResourceKeyId\" = {resourceKeyId} ORDER BY \"DocumentId\" LIMIT 25"
        );
        IReadOnlyList<SpikeParameterSpec> empty =
        [
            .. batch.Parameters!.Select(parameter =>
                parameter.BareName == "offset"
                    ? parameter with
                    {
                        Value = Convert.ChangeType(
                            1_000_000,
                            parameter.Value!.GetType(),
                            CultureInfo.InvariantCulture
                        ),
                    }
                    : parameter
            ),
        ];

        JsonArray results = [];
        JsonArray prepared = [];
        Probe probe = new(null!, null!, null!, context.LeasedConnectionString);
        foreach (string mode in PlanCacheModes)
        {
            await using NpgsqlDataSource pool = probe.Pool($"descriptor-plan-cache-{mode}");
            await probe.ExecuteAsync(pool, $"SET plan_cache_mode = '{mode}';");
            for (int execution = 1; execution <= Executions; execution++)
            {
                async Task RunPairAsync()
                {
                    // Non-empty, then empty, on the same connection: the empty page must see no stale ids.
                    List<List<object?[]>> full = await probe.RawAsync(
                        pool,
                        batch.CommandText,
                        batch.Parameters!
                    );
                    List<List<object?[]>> none = await probe.RawAsync(pool, batch.CommandText, empty);
                    results.Add(DescriptorCheck(mode, execution, "25 @0", full, expected));
                    results.Add(DescriptorCheck(mode, execution, "past the end", none, []));
                }

                if (execution == Executions && AutoExplainWindow.Enabled)
                {
                    await AutoExplainWindow.CaptureAsync(
                        context.OpenConnectionAsync,
                        run,
                        $"descriptor-probe-{mode}",
                        RunPairAsync
                    );
                }
                else
                {
                    await RunPairAsync();
                }
            }

            prepared.Add(
                new JsonObject { ["mode"] = mode, ["statements"] = await probe.PreparedStatementsAsync(pool) }
            );
        }

        return Finish(run, results, prepared);
    }

    /// <summary>
    /// The descriptor batch's result sets are: the selection boundary, the leading versions, the owner
    /// rows, and the trailing versions. The ids of each version set and the boundary must be exact.
    /// </summary>
    private static JsonObject DescriptorCheck(
        string mode,
        int execution,
        string name,
        List<List<object?[]>> sets,
        long[] expected
    )
    {
        long? boundary = sets[0].Count == 1 && sets[0][0][0] is long value ? value : null;
        long[] leading = [.. sets[1].Select(row => Convert.ToInt64(row[0], CultureInfo.InvariantCulture))];
        long[] trailing = [.. sets[^1].Select(row => Convert.ToInt64(row[0], CultureInfo.InvariantCulture))];
        bool passed =
            sets.Count == 4
            && leading.SequenceEqual(expected)
            && trailing.SequenceEqual(expected)
            && sets[2].Count == expected.Length
            && boundary == (expected.Length == 0 ? null : expected.Max());
        return new JsonObject
        {
            ["group"] = $"descriptor plan-cache {mode}",
            ["probe"] = $"{name} #{execution}",
            ["resultSets"] = sets.Count,
            ["expectedCount"] = expected.Length,
            ["leadingCount"] = leading.Length,
            ["ownerRows"] = sets.Count > 2 ? sets[2].Count : -1,
            ["boundary"] = boundary,
            ["passed"] = passed,
        };
    }

    private static PageKeysetSpec.SelectedPage Selected(long[] ids) => new(ids);

    private static JsonObject Check(
        string group,
        string name,
        long[] expected,
        ProbeResult result,
        int? expectedPid
    ) =>
        new()
        {
            ["group"] = group,
            ["probe"] = name,
            ["expectedCount"] = expected.Length,
            ["actualCount"] = result.Ids.Length,
            ["exact"] = result.Ids.SequenceEqual(expected),
            ["carrierAfter"] = result.CarrierAfter,
            ["backendPid"] = result.BackendPid,
            ["passed"] =
                result.Ids.SequenceEqual(expected)
                && string.IsNullOrEmpty(result.CarrierAfter)
                && (expectedPid is null || expectedPid == result.BackendPid),
        };

    private static string Finish(RunArtifacts run, JsonArray results, JsonArray prepared)
    {
        run.WriteJson("probes.json", results);
        run.WriteJson("prepared-statements.json", prepared);
        int failed = results.Count(result => !result!["passed"]!.GetValue<bool>());
        run.Manifest["probes"] = results.Count;
        run.Manifest["probesFailed"] = failed;
        run.Manifest["completedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        run.WriteJson("run-manifest.json", run.Manifest);
        Require(failed == 0, $"{failed} of {results.Count} probes failed; see {run.Directory}/probes.json");
        return run.Directory;
    }

    private static async Task GetAsync(KeysetSpikeContext context, string url)
    {
        using HttpResponseMessage response = await context.Harness.HttpClient.GetAsync(url);
        Require(response.StatusCode == HttpStatusCode.OK, $"GET {url}: HTTP {(int)response.StatusCode}");
    }

    private static Task<long[]> AllStudentIdsAsync(DbConnection connection) =>
        ScalarListAsync(
            connection,
            "SELECT \"DocumentId\" FROM \"edfi\".\"Student\" ORDER BY \"DocumentId\""
        );

    private static async Task<long[]> ScalarListAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        List<long> ids = [];
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return [.. ids];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record ProbeResult(long[] Ids, string? CarrierAfter, int BackendPid, int Attempts);

    private sealed class Probe(
        ResourceReadPlan plan,
        HydrationExecutionOptions options,
        PageKeysetSpec.Query query,
        string connectionString
    )
    {
        /// <summary>One physical connection, built with NpgsqlDataSourceLifetime's settings.</summary>
        public NpgsqlDataSource Pool(string name)
        {
            NpgsqlDataSourceBuilder builder = new(connectionString);
            builder.ConnectionStringBuilder.NoResetOnClose = true;
            builder.ConnectionStringBuilder.AutoPrepareMinUsages = 3;
            builder.ConnectionStringBuilder.MaxAutoPrepare = 256;
            builder.ConnectionStringBuilder.MaxPoolSize = 1;
            builder.ConnectionStringBuilder.ApplicationName = $"DMS1521.CarrierProbe.{name}";
            return builder.Build();
        }

        public PageKeysetSpec.Query Query(long limit, long offset)
        {
            Dictionary<string, object?> values = new(query.ParameterValues);
            Require(
                values.ContainsKey("limit") && values.ContainsKey("offset"),
                "the query keyset has no limit/offset"
            );
            values["limit"] = Convert.ChangeType(
                limit,
                values["limit"]!.GetType(),
                CultureInfo.InvariantCulture
            );
            values["offset"] = Convert.ChangeType(
                offset,
                values["offset"]!.GetType(),
                CultureInfo.InvariantCulture
            );
            return query with { ParameterValues = values };
        }

        public async Task<ProbeResult> HydrateAsync(
            NpgsqlDataSource pool,
            PageKeysetSpec keyset,
            Func<string, int, string>? rewrite = null
        )
        {
            await using NpgsqlConnection connection = await pool.OpenConnectionAsync();
            int attempts = 0;
            HydratedPage page = await ConsistentHydrationExecutor.ExecuteAsync(
                sql =>
                {
                    attempts++;
                    DbCommand command = connection.CreateCommand();
                    command.CommandText = rewrite is null ? sql : rewrite(sql, attempts);
                    return command;
                },
                plan,
                keyset,
                SqlDialect.Pgsql,
                options,
                CancellationToken.None
            );
            return new ProbeResult(
                [.. page.DocumentMetadata.Select(row => row.DocumentId)],
                await CarrierAsync(connection),
                connection.ProcessID,
                attempts
            );
        }

        public async Task<string?> CarrierAsync(NpgsqlDataSource pool)
        {
            await using NpgsqlConnection connection = await pool.OpenConnectionAsync();
            return await CarrierAsync(connection);
        }

        private static async Task<string?> CarrierAsync(NpgsqlConnection connection)
        {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT current_setting('{CarrierSetting}', true);";
            return await command.ExecuteScalarAsync() as string;
        }

        public async Task ExecuteAsync(NpgsqlDataSource pool, string sql)
        {
            await using NpgsqlConnection connection = await pool.OpenConnectionAsync();
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async Task<List<List<object?[]>>> RawAsync(
            NpgsqlDataSource pool,
            string sql,
            IReadOnlyList<SpikeParameterSpec> parameters
        )
        {
            await using NpgsqlConnection connection = await pool.OpenConnectionAsync();
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (SpikeParameterSpec parameter in parameters)
            {
                parameter.Bind(command);
            }

            List<List<object?[]>> sets = [];
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            do
            {
                if (reader.FieldCount == 0)
                {
                    continue;
                }

                List<object?[]> rows = [];
                while (await reader.ReadAsync())
                {
                    object?[] values = new object?[reader.FieldCount];
                    reader.GetValues(values!);
                    rows.Add([.. values.Select(value => value is DBNull ? null : value)]);
                }

                sets.Add(rows);
            } while (await reader.NextResultAsync());

            return sets;
        }

        /// <summary>
        /// pg_prepared_statements on the probe's own physical connection (the view is session-local), with
        /// each statement classified as parameterized or carrier-only.
        /// </summary>
        public async Task<JsonArray> PreparedStatementsAsync(NpgsqlDataSource pool)
        {
            await using NpgsqlConnection connection = await pool.OpenConnectionAsync();
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT statement, parameter_types::text[], generic_plans, custom_plans FROM pg_prepared_statements WHERE NOT from_sql ORDER BY prepare_time;";
            JsonArray statements = [];
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string text = reader.GetString(0);
                string[] types = reader.GetFieldValue<string[]>(1);
                statements.Add(
                    new JsonObject
                    {
                        ["class"] = types.Length == 0 ? "carrier-only" : "parameterized",
                        ["readsKeyset"] = text.Contains("current_setting", StringComparison.Ordinal),
                        ["parameterTypes"] = new JsonArray([.. types.Select(type => (JsonNode)type)]),
                        ["genericPlans"] = reader.GetInt64(2),
                        ["customPlans"] = reader.GetInt64(3),
                        ["statement"] = text.Length > 220 ? text[..220] : text,
                    }
                );
            }

            return statements;
        }
    }
}
