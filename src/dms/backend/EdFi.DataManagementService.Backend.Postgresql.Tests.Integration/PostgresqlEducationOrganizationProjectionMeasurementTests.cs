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
[TestFixture]
[Explicit("Provider measurement at the projection cap; run on demand and record the output.")]
[Category("ProjectionMeasurement")]
public class Given_A_Postgresql_Education_Organization_Projection_Set_At_The_Cap
    : PostgresqlEducationOrganizationProjectionFixtureBase
{
    private const int Cap = 50_000;
    private const int LocalEducationAgencies = 989;
    private const int WarmupReads = 2;
    private const int MeasuredReads = 10;
    private const int WriterUpdates = 2000;

    [Test]
    public async Task It_measures_reads_and_concurrent_writer_impact()
    {
        var seeding = Stopwatch.StartNew();
        await Seed.BulkHierarchyAsync(Cap, LocalEducationAgencies);
        seeding.Stop();
        Database.CommandTimeoutSeconds = 30;

        string applicationName = "projection-measure-" + Guid.NewGuid().ToString("N");
        string readerConnectionString = new NpgsqlConnectionStringBuilder(Database.ConnectionString)
        {
            ApplicationName = applicationName,
            MinPoolSize = 0,
        }.ConnectionString;
        var reader = Create(readerConnectionString, Logger);
        var request = Request(Fixture.MappingSet, maxProjectionRows: Cap);
        using var bytesRead = new NpgsqlBytesRead(applicationName);

        for (int index = 0; index < WarmupReads; index++)
        {
            (await reader.ReadSetAsync(request, CancellationToken.None))
                .Should()
                .BeOfType<Result.Set>()
                .Which.Rows.Should()
                .HaveCount(Cap);
        }

        List<double> readMilliseconds = [];
        List<long> readBytes = [];
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

        var alone = await RunWriterAsync(CancellationToken.None);

        using var stopReads = new CancellationTokenSource();
        ConcurrentBag<string> outcomes = [];
        var readLoop = Task.Run(async () =>
        {
            while (!stopReads.IsCancellationRequested)
            {
                var result = await reader.ReadSetAsync(request, CancellationToken.None);
                outcomes.Add(
                    result switch
                    {
                        Result.Set => "Set",
                        Result.TargetUnavailable unavailable => $"Unavailable({unavailable.Describe})",
                        _ => result.GetType().Name,
                    }
                );
            }
        });
        var concurrent = await RunWriterAsync(CancellationToken.None);
        await stopReads.CancelAsync();
        await readLoop;

        var output = TestContext.Out;
        await output.WriteLineAsync(
            $"MEASURE engine=postgresql rows={Cap} schools={Cap - 11 - LocalEducationAgencies} seed_s={seeding.Elapsed.TotalSeconds:F1}"
        );
        await output.WriteLineAsync($"MEASURE read_ms n={MeasuredReads} {Summary(readMilliseconds)}");
        await output.WriteLineAsync(
            $"MEASURE read_bytes {Summary(readBytes.Select(static value => (double)value).ToList())} per_row_median={Median(readBytes.Select(static value => (double)value).ToList()) / Cap:F1}"
        );
        await output.WriteLineAsync(
            $"MEASURE writer_alone_ms n={WriterUpdates} {Summary(alone.Milliseconds)} errors={Errors(alone)}"
        );
        await output.WriteLineAsync(
            $"MEASURE writer_with_reads_ms n={WriterUpdates} {Summary(concurrent.Milliseconds)} errors={Errors(concurrent)} reads_during={outcomes.Count} outcomes={string.Join(",", outcomes.GroupBy(static outcome => outcome).Select(static group => $"{group.Key}:{group.Count()}"))}"
        );
    }

    private async Task<WriterRun> RunWriterAsync(CancellationToken cancellationToken)
    {
        var random = new Random(1440);
        int schools = Cap - 11 - LocalEducationAgencies;
        List<double> milliseconds = [];
        ConcurrentDictionary<string, int> errors = new();

        for (int index = 0; index < WriterUpdates; index++)
        {
            long schoolId = 1_000_000 + random.Next(1, schools + 1);
            var timer = Stopwatch.StartNew();
            try
            {
                await using var connection = new NpgsqlConnection(Database.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand(
                    """UPDATE "edfi"."School" SET "NameOfInstitution" = @name WHERE "SchoolId" = @id;""",
                    connection
                );
                command.CommandTimeout = 120;
                command.Parameters.AddWithValue("name", $"Measurement School Renamed {index}");
                command.Parameters.AddWithValue("id", schoolId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException exception)
            {
                errors.AddOrUpdate(exception.SqlState, 1, static (_, count) => count + 1);
            }

            milliseconds.Add(timer.Elapsed.TotalMilliseconds);
        }

        return new(milliseconds, errors);
    }

    private static string Summary(List<double> values)
    {
        var sorted = values.Order().ToList();
        return $"min={sorted[0]:F1} p50={Median(sorted):F1} p95={Percentile(sorted, 0.95):F1} max={sorted[^1]:F1}";
    }

    private static double Median(List<double> values) => Percentile(values.Order().ToList(), 0.5);

    private static double Percentile(List<double> sorted, double percentile) =>
        sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(percentile * sorted.Count) - 1)];

    private static string Errors(WriterRun run) =>
        run.Failures.IsEmpty
            ? "none"
            : string.Join(",", run.Failures.Select(static error => $"{error.Key}:{error.Value}"));

    private sealed record WriterRun(List<double> Milliseconds, ConcurrentDictionary<string, int> Failures);

    /// <summary>
    /// Npgsql's cumulative <c>db.client.commands.bytes_read</c> for the data sources whose name carries
    /// the application name, observed on demand.
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
