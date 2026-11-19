// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Postgresql.Tests.Integration.PostgresqlProjectionReaders;
using Result = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSetResult;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// The step 2.5 provider measurement at the default cap. Opt-in: it seeds 50,000 rows and prints
/// <c>MEASURE</c> lines for the step report and the documentation rather than asserting budgets.
/// </summary>
/// <remarks>
/// The writer workload covers all four tables: concurrent writers rotate through single-row name
/// updates of a state agency, a service center, a local agency and a school, and a two-table
/// transaction that updates a school and then a local agency. It runs alone and then during
/// back-to-back reads. PostgreSQL keeps no cumulative lock-wait statistic, so lock waits are sampled
/// from <c>pg_stat_activity</c>; deadlocks come from <c>pg_stat_database</c>.
/// </remarks>
[TestFixture]
[Explicit("Provider measurement at the projection cap; run on demand and record the output.")]
[Category("ProjectionMeasurement")]
public class Given_A_Postgresql_Education_Organization_Projection_Set_At_The_Cap
    : PostgresqlEducationOrganizationProjectionFixtureBase
{
    private const int Cap = 50_000;
    private const int LocalEducationAgencies = 989;
    private const int Schools = Cap - 11 - LocalEducationAgencies;
    private const int WarmupReads = 2;
    private const int MeasuredReads = 10;
    private const int Writers = 4;
    private const int OperationsPerWriter = 500;
    private const int SampleIntervalMilliseconds = 20;

    // PROJECTION_MEASURE_KINDS (comma-separated) narrows the writer mix, for example to compare the
    // single-row writers alone with the full mix that includes the two-table transaction.
    private static readonly string[] _kinds =
        Environment
            .GetEnvironmentVariable("PROJECTION_MEASURE_KINDS")
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries)
        ?? ["sea", "esc", "lea", "school", "school+lea"];

    [Test]
    public async Task It_measures_reads_and_concurrent_writer_impact()
    {
        var seeding = Stopwatch.StartNew();
        await Seed.BulkHierarchyAsync(Cap, LocalEducationAgencies);
        seeding.Stop();
        Database.CommandTimeoutSeconds = 30;

        string tag = Guid.NewGuid().ToString("N");
        string readerApplication = "projection-measure-reader-" + tag;
        string writerApplication = "projection-measure-writer-" + tag;
        string readerConnectionString = new NpgsqlConnectionStringBuilder(Database.ConnectionString)
        {
            ApplicationName = readerApplication,
            MinPoolSize = 0,
        }.ConnectionString;
        string writerConnectionString = new NpgsqlConnectionStringBuilder(Database.ConnectionString)
        {
            ApplicationName = writerApplication,
            MinPoolSize = 0,
        }.ConnectionString;
        var reader = Create(readerConnectionString, Logger);
        var request = Request(Fixture.MappingSet, maxProjectionRows: Cap);
        using var bytesRead = new NpgsqlBytesRead(readerApplication);

        for (int index = 0; index < WarmupReads; index++)
        {
            (await reader.ReadSetAsync(request, CancellationToken.None))
                .Should()
                .BeOfType<Result.Set>()
                .Which.Rows.Should()
                .HaveCount(Cap);
        }

        List<double> readMilliseconds = [];
        List<double> readBytes = [];
        for (int index = 0; index < MeasuredReads; index++)
        {
            long before = bytesRead.Total();
            var timer = Stopwatch.StartNew();
            var result = await reader.ReadSetAsync(request, CancellationToken.None);
            timer.Stop();
            readBytes.Add(bytesRead.Total() - before);
            readMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
            result.Should().BeOfType<Result.Set>().Which.Rows.Should().HaveCount(Cap);
        }

        var alone = await RunPhaseAsync(writerConnectionString, writerApplication, readerApplication, null);
        var withReads = await RunPhaseAsync(
            writerConnectionString,
            writerApplication,
            readerApplication,
            () => reader.ReadSetAsync(request, CancellationToken.None)
        );

        var output = TestContext.Out;
        await output.WriteLineAsync(
            $"MEASURE engine=postgresql rows={Cap} schools={Schools} seed_s={seeding.Elapsed.TotalSeconds:F1}"
        );
        await output.WriteLineAsync($"MEASURE read_ms n={MeasuredReads} {Summary(readMilliseconds)}");
        await output.WriteLineAsync(
            $"MEASURE read_bytes {Summary(readBytes)} per_row_median={Percentile(readBytes, 0.5) / Cap:F1}"
        );
        await output.WriteLineAsync(alone.Describe("writers_alone"));
        await output.WriteLineAsync(withReads.Describe("writers_with_reads"));
    }

    private async Task<Phase> RunPhaseAsync(
        string writerConnectionString,
        string writerApplication,
        string readerApplication,
        Func<Task<Result>>? read
    )
    {
        long deadlocksBefore = await DeadlocksAsync();
        var phase = new Phase();
        using var stop = new CancellationTokenSource();

        var sampler = Task.Run(() => SampleAsync(phase, writerApplication, readerApplication, stop.Token));
        var readLoop = read is null
            ? Task.CompletedTask
            : Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var timer = Stopwatch.StartNew();
                    var result = await read();
                    phase.ReadMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
                    phase.ReadOutcomes.Add(
                        result switch
                        {
                            Result.Set => "Set",
                            Result.TargetUnavailable unavailable => $"Unavailable({unavailable.Describe})",
                            _ => result.GetType().Name,
                        }
                    );
                }
            });

        await Task.WhenAll(
            Enumerable
                .Range(0, Writers)
                .Select(writer => Task.Run(() => WriteAsync(writer, writerConnectionString, phase)))
        );
        await stop.CancelAsync();
        await readLoop;
        await sampler;

        // Statistics reach pg_stat_database when a backend reports them, at most about a second later.
        await Task.Delay(TimeSpan.FromSeconds(2));
        phase.Deadlocks = await DeadlocksAsync() - deadlocksBefore;
        return phase;
    }

    private static async Task WriteAsync(int writer, string connectionString, Phase phase)
    {
        var random = new Random(1440 + writer);
        for (int index = 0; index < OperationsPerWriter; index++)
        {
            string kind = _kinds[(index + writer) % _kinds.Length];
            string name = $"Measurement Renamed {writer}-{index}";
            var timer = Stopwatch.StartNew();
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                if (kind == "school+lea")
                {
                    await using var transaction = await connection.BeginTransactionAsync();
                    await UpdateAsync(
                        connection,
                        "School",
                        "SchoolId",
                        1_000_000 + random.Next(1, Schools + 1),
                        name
                    );
                    await UpdateAsync(
                        connection,
                        "LocalEducationAgency",
                        "LocalEducationAgencyId",
                        200_000 + random.Next(1, LocalEducationAgencies + 1),
                        name
                    );
                    await transaction.CommitAsync();
                }
                else
                {
                    (string table, string column, long id) = kind switch
                    {
                        "sea" => ("StateEducationAgency", "StateEducationAgencyId", 1L),
                        "esc" => (
                            "EducationServiceCenter",
                            "EducationServiceCenterId",
                            10L + random.Next(1, 11)
                        ),
                        "lea" => (
                            "LocalEducationAgency",
                            "LocalEducationAgencyId",
                            200_000L + random.Next(1, LocalEducationAgencies + 1)
                        ),
                        _ => ("School", "SchoolId", 1_000_000L + random.Next(1, Schools + 1)),
                    };
                    await UpdateAsync(connection, table, column, id, name);
                }
            }
            catch (PostgresException exception)
            {
                phase.Errors.AddOrUpdate(exception.SqlState, 1, static (_, count) => count + 1);
            }

            phase.WriteMilliseconds.GetOrAdd(kind, static _ => []).Add(timer.Elapsed.TotalMilliseconds);
        }
    }

    private static async Task UpdateAsync(
        NpgsqlConnection connection,
        string table,
        string column,
        long id,
        string name
    )
    {
        await using var command = new NpgsqlCommand(
            $"""UPDATE "edfi"."{table}" SET "NameOfInstitution" = @name WHERE "{column}" = @id;""",
            connection
        );
        command.CommandTimeout = 120;
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SampleAsync(
        Phase phase,
        string writerApplication,
        string readerApplication,
        CancellationToken stop
    )
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FILTER (WHERE application_name = @writer AND wait_event_type = 'Lock'),
                   count(*) FILTER (WHERE application_name = @reader AND wait_event_type = 'Lock')
            FROM pg_stat_activity;
            """,
            connection
        );
        command.Parameters.AddWithValue("writer", writerApplication);
        command.Parameters.AddWithValue("reader", readerApplication);

        while (!stop.IsCancellationRequested)
        {
            await using (var reader = await command.ExecuteReaderAsync())
            {
                await reader.ReadAsync();
                phase.Sample(reader.GetInt64(0), reader.GetInt64(1));
            }

            await Task.Delay(SampleIntervalMilliseconds);
        }
    }

    private async Task<long> DeadlocksAsync() =>
        await Database.ExecuteScalarAsync<long>(
            "SELECT deadlocks FROM pg_stat_database WHERE datname = current_database();"
        );

    private static string Summary(List<double> values)
    {
        if (values.Count == 0)
        {
            return "n=0";
        }

        var sorted = values.Order().ToList();
        return $"min={sorted[0]:F1} p50={Percentile(sorted, 0.5):F1} p95={Percentile(sorted, 0.95):F1} max={sorted[^1]:F1}";
    }

    private static double Percentile(List<double> values, double percentile)
    {
        var sorted = values.Order().ToList();
        return sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(percentile * sorted.Count) - 1)];
    }

    private sealed class Phase
    {
        private long _samples;
        private long _writerWaitingSamples;
        private long _writerWaitingSessions;
        private long _writerWaitingMax;
        private long _readerWaitingSamples;

        public ConcurrentDictionary<string, ConcurrentBag<double>> WriteMilliseconds { get; } = new();
        public ConcurrentDictionary<string, int> Errors { get; } = new();
        public ConcurrentBag<double> ReadMilliseconds { get; } = [];
        public ConcurrentBag<string> ReadOutcomes { get; } = [];
        public long Deadlocks { get; set; }

        public void Sample(long writersWaiting, long readerWaiting)
        {
            _samples++;
            if (writersWaiting > 0)
            {
                _writerWaitingSamples++;
                _writerWaitingSessions += writersWaiting;
                _writerWaitingMax = Math.Max(_writerWaitingMax, writersWaiting);
            }

            if (readerWaiting > 0)
            {
                _readerWaitingSamples++;
            }
        }

        public string Describe(string name)
        {
            var all = WriteMilliseconds.Values.SelectMany(static values => values).ToList();
            string byKind = string.Join(
                " ",
                _kinds.Select(kind => $"{kind}_p95={Percentile([.. WriteMilliseconds[kind]], 0.95):F1}")
            );
            string errors = Errors.IsEmpty
                ? "none"
                : string.Join(",", Errors.Select(static error => $"{error.Key}:{error.Value}"));
            string outcomes = ReadOutcomes.IsEmpty
                ? "none"
                : string.Join(
                    ",",
                    ReadOutcomes
                        .GroupBy(static outcome => outcome)
                        .Select(static group => $"{group.Key}:{group.Count()}")
                );
            return $"MEASURE phase={name} writers={Writers} kinds={string.Join('/', _kinds)} ops={all.Count} op_ms {Summary(all)} {byKind} "
                + $"errors={errors} deadlocks={Deadlocks} "
                + $"sampled_lock_waits: writer_waiting_samples={_writerWaitingSamples}/{_samples} "
                + $"writer_waiting_session_samples={_writerWaitingSessions} writer_waiting_max={_writerWaitingMax} "
                + $"reader_waiting_samples={_readerWaitingSamples}/{_samples} interval_ms={SampleIntervalMilliseconds} "
                + $"reads={ReadOutcomes.Count} read_ms {Summary([.. ReadMilliseconds])} outcomes={outcomes}";
        }
    }

    /// <summary>
    /// Npgsql's <c>db.client.commands.bytes_read</c> for the data sources whose name carries the
    /// application name, observed on demand.
    /// </summary>
    private sealed class NpgsqlBytesRead : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly string _applicationName;
        private readonly ConcurrentDictionary<string, long> _latest = new();

        public NpgsqlBytesRead(string applicationName)
        {
            _applicationName = applicationName;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Npgsql" && instrument.Name == "db.client.commands.bytes_read")
                {
                    listener.EnableMeasurementEvents(instrument, instrument.IsObservable);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (_, value, tags, state) =>
                {
                    string key = string.Join(
                        "|",
                        tags.ToArray().Select(static tag => $"{tag.Key}={tag.Value}")
                    );
                    // An observable counter reports its running total; a plain one reports increments.
                    if (state is true)
                    {
                        _latest[key] = value;
                    }
                    else
                    {
                        _latest.AddOrUpdate(key, value, (_, total) => total + value);
                    }
                }
            );
            _listener.Start();
        }

        public long Total()
        {
            _listener.RecordObservableInstruments();
            return _latest
                .Where(entry => entry.Key.Contains(_applicationName, StringComparison.Ordinal))
                .Sum(static entry => entry.Value);
        }

        public void Dispose() => _listener.Dispose();
    }
}
