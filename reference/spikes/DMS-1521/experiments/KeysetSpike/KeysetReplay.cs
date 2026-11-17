// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace KeysetSpike;

/// <summary>
/// One captured batch, ready to replay: its kind, page size, text, and binding specifications. A
/// two-phase form (task 8 screening) sets <paramref name="SecondPhase" />: the ids in the first
/// result set build a second batch, which runs on the same connection as part of the same request.
/// </summary>
public sealed record ReplayBatch(
    string Kind,
    int PageSize,
    string Text,
    IReadOnlyList<SpikeParameterSpec> Parameters,
    Func<IReadOnlyList<long>, ReplayBatch>? SecondPhase = null
);

public sealed record ReplayResult(
    int Concurrency,
    int WarmupPerWorker,
    int IterationsPerWorker,
    int Executions,
    int WarmupErrors,
    int Errors,
    IReadOnlyList<string> ErrorSamples,
    double WallMs,
    double ThroughputPerSecond,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MeanMs,
    double MaxMs
)
{
    public JsonObject ToJson() =>
        new()
        {
            ["concurrency"] = Concurrency,
            ["warmupPerWorker"] = WarmupPerWorker,
            ["iterationsPerWorker"] = IterationsPerWorker,
            ["executions"] = Executions,
            ["warmupErrors"] = WarmupErrors,
            ["errors"] = Errors,
            ["errorSamples"] = new JsonArray([.. ErrorSamples.Select(sample => (JsonNode)sample)]),
            ["wallMs"] = WallMs,
            ["throughputPerSecond"] = ThroughputPerSecond,
            ["p50Ms"] = P50Ms,
            ["p95Ms"] = P95Ms,
            ["p99Ms"] = P99Ms,
            ["meanMs"] = MeanMs,
            ["maxMs"] = MaxMs,
        };
}

/// <summary>
/// The connection source a replay runs on, built the way production builds it: Npgsql with the
/// three <c>NpgsqlDataSourceLifetime</c> settings (NoResetOnClose, AutoPrepareMinUsages 3,
/// MaxAutoPrepare 256), and SqlClient with the leased connection string unchanged, as
/// <c>MssqlConnectionAcquisition</c> uses it for a primary target. Each execution opens and closes
/// a pooled connection, as a request does.
/// </summary>
public sealed class ReplayTarget : IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly string _connectionString;

    public ReplayTarget(PerfProvider provider, string connectionString)
    {
        Provider = provider;
        _connectionString = connectionString;
        if (provider == PerfProvider.Postgresql)
        {
            NpgsqlDataSourceBuilder builder = new(connectionString);
            builder.ConnectionStringBuilder.NoResetOnClose = true;
            builder.ConnectionStringBuilder.ApplicationName = "EdFi.DMS.KeysetReplay";
            builder.ConnectionStringBuilder.AutoPrepareMinUsages = 3;
            builder.ConnectionStringBuilder.MaxAutoPrepare = 256;
            _dataSource = builder.Build();
        }
    }

    public PerfProvider Provider { get; }

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        if (_dataSource is not null)
        {
            return await _dataSource.OpenConnectionAsync(ct);
        }

        SqlConnection connection = new(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }
}

/// <summary>
/// Replays one captured batch at a fixed concurrency. Every worker first runs its warmup
/// executions; then all workers run their measured executions together, and throughput is the
/// measured executions over that phase's wall time. An optional window (a counter window) wraps the
/// measured phase only. Every result set is read to the end, as the
/// production reader does. A failed execution is counted, not retried.
/// </summary>
public static class KeysetReplay
{
    public static async Task<ReplayResult> RunAsync(
        ReplayTarget target,
        ReplayBatch batch,
        int concurrency,
        int warmupPerWorker,
        int iterationsPerWorker,
        Func<Func<Task>, Task>? measuredPhaseWindow = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterationsPerWorker, 1);

        List<string> errorSamples = [];
        int errors = 0;

        async Task<double?> ExecuteOnceAsync()
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                await using DbConnection connection = await target.OpenAsync();
                IReadOnlyList<long> firstResultIds = await ExecuteAsync(connection, batch);
                if (batch.SecondPhase is not null)
                {
                    await ExecuteAsync(connection, batch.SecondPhase(firstResultIds));
                }

                return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            catch (DbException exception)
            {
                Interlocked.Increment(ref errors);
                lock (errorSamples)
                {
                    if (errorSamples.Count < 5)
                    {
                        errorSamples.Add(exception.Message);
                    }
                }

                return null;
            }
        }

        await Task.WhenAll(
            Enumerable
                .Range(0, concurrency)
                .Select(async _ =>
                {
                    for (int iteration = 0; iteration < warmupPerWorker; iteration++)
                    {
                        await ExecuteOnceAsync();
                    }
                })
        );

        int warmupErrors = errors;
        List<double> samples = [];
        Stopwatch wall = new();

        async Task MeasuredPhaseAsync()
        {
            wall.Start();
            await Task.WhenAll(
                Enumerable
                    .Range(0, concurrency)
                    .Select(async _ =>
                    {
                        for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
                        {
                            double? elapsed = await ExecuteOnceAsync();
                            if (elapsed is double value)
                            {
                                lock (samples)
                                {
                                    samples.Add(value);
                                }
                            }
                        }
                    })
            );
            wall.Stop();
        }

        // The warmup above always runs outside any counter window: a window wraps only the measured
        // phase, so the compiles and recompiles it records happened after warmup.
        if (measuredPhaseWindow is null)
        {
            await MeasuredPhaseAsync();
        }
        else
        {
            await measuredPhaseWindow(MeasuredPhaseAsync);
        }

        double[] sorted = [.. samples.Order()];
        return new ReplayResult(
            concurrency,
            warmupPerWorker,
            iterationsPerWorker,
            concurrency * iterationsPerWorker,
            warmupErrors,
            errors - warmupErrors,
            errorSamples,
            wall.Elapsed.TotalMilliseconds,
            sorted.Length / wall.Elapsed.TotalSeconds,
            NearestRank(sorted, 50),
            NearestRank(sorted, 95),
            NearestRank(sorted, 99),
            sorted.Length == 0 ? double.NaN : sorted.Average(),
            sorted.Length == 0 ? double.NaN : sorted[^1]
        );
    }

    /// <summary>
    /// Runs one batch and reads every result set to the end. Returns the first result set's first
    /// column when the batch has a second phase that needs it, and an empty list otherwise.
    /// </summary>
    public static async Task<IReadOnlyList<long>> ExecuteAsync(DbConnection connection, ReplayBatch batch)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = batch.Text;
        command.CommandTimeout = 600;
        foreach (SpikeParameterSpec parameter in batch.Parameters)
        {
            parameter.Bind(command);
        }

        List<long> firstResultIds = [];
        bool first = true;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        do
        {
            while (await reader.ReadAsync())
            {
                if (first && batch.SecondPhase is not null)
                {
                    firstResultIds.Add(reader.GetInt64(0));
                }
            }

            first = false;
        } while (await reader.NextResultAsync());

        return firstResultIds;
    }

    private static double NearestRank(double[] sorted, int percentile)
    {
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        int rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Max(rank, 1) - 1];
    }
}
