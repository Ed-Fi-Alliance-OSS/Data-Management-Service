// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Mssql.Tests.Integration.MssqlProjectionReaders;
using Result = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSetResult;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The step 2.5 provider measurement at the default cap. Opt-in: it seeds 50,000 rows and prints
/// <c>MEASURE</c> lines for the step report and the documentation rather than asserting budgets.
/// </summary>
[TestFixture]
[Explicit("Provider measurement at the projection cap; run on demand and record the output.")]
[Category("ProjectionMeasurement")]
public class Given_A_Mssql_Education_Organization_Projection_Set_At_The_Cap
    : MssqlEducationOrganizationProjectionFixtureBase
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

        string readerConnectionString = new SqlConnectionStringBuilder(Database.ConnectionString)
        {
            ApplicationName = "projection-measure-" + Guid.NewGuid().ToString("N"),
            MinPoolSize = 0,
        }.ConnectionString;
        var request = Request(Fixture.MappingSet, maxProjectionRows: Cap);
        long lastBytes = 0;
        var observer = new RecordingProjectionReadObserver
        {
            OnAfterAcquire = static (connection, _) =>
            {
                var sqlConnection = (SqlConnection)connection;
                sqlConnection.StatisticsEnabled = true;
                sqlConnection.ResetStatistics();
                return Task.CompletedTask;
            },
            OnBeforeCommit = (connection, _) =>
            {
                lastBytes = Convert.ToInt64(
                    ((SqlConnection)connection).RetrieveStatistics()["BytesReceived"]
                );
                return Task.CompletedTask;
            },
        };
        var reader = Create(readerConnectionString, Logger, observer);

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
            var timer = Stopwatch.StartNew();
            var result = await reader.ReadSetAsync(request, CancellationToken.None);
            timer.Stop();
            readMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
            readBytes.Add(lastBytes);
            result.Should().BeOfType<Result.Set>().Which.Rows.Should().HaveCount(Cap);
        }

        var alone = await RunWriterAsync();

        var plainReader = Create(readerConnectionString, Logger);
        using var stopReads = new CancellationTokenSource();
        ConcurrentBag<string> outcomes = [];
        var readLoop = Task.Run(async () =>
        {
            while (!stopReads.IsCancellationRequested)
            {
                var result = await plainReader.ReadSetAsync(request, CancellationToken.None);
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
        var concurrent = await RunWriterAsync();
        await stopReads.CancelAsync();
        await readLoop;

        var output = TestContext.Out;
        await output.WriteLineAsync(
            $"MEASURE engine=sqlserver rows={Cap} schools={Cap - 11 - LocalEducationAgencies} seed_s={seeding.Elapsed.TotalSeconds:F1}"
        );
        await output.WriteLineAsync($"MEASURE read_ms n={MeasuredReads} {Summary(readMilliseconds)}");
        await output.WriteLineAsync(
            $"MEASURE read_bytes {Summary(readBytes)} per_row_median={Median(readBytes) / Cap:F1}"
        );
        await output.WriteLineAsync(
            $"MEASURE writer_alone_ms n={WriterUpdates} {Summary(alone.Milliseconds)} errors={Errors(alone)}"
        );
        await output.WriteLineAsync(
            $"MEASURE writer_with_reads_ms n={WriterUpdates} {Summary(concurrent.Milliseconds)} errors={Errors(concurrent)} reads_during={outcomes.Count} outcomes={string.Join(",", outcomes.GroupBy(static outcome => outcome).Select(static group => $"{group.Key}:{group.Count()}"))}"
        );
    }

    private async Task<WriterRun> RunWriterAsync()
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
                await using var connection = new SqlConnection(Database.ConnectionString);
                await connection.OpenAsync();
                await using var command = new SqlCommand(
                    "UPDATE [edfi].[School] SET [NameOfInstitution] = @name WHERE [SchoolId] = @id;",
                    connection
                );
                command.CommandTimeout = 120;
                command.Parameters.AddWithValue("@name", $"Measurement School Renamed {index}");
                command.Parameters.AddWithValue("@id", schoolId);
                await command.ExecuteNonQueryAsync();
            }
            catch (SqlException exception)
            {
                errors.AddOrUpdate(
                    exception.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    1,
                    static (_, count) => count + 1
                );
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
}
