// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Real-PostgreSQL execution evidence for the compiled partition-boundary statement.
/// </summary>
/// <remarks>
/// The statement is produced by the production planner and compiler, not hand-written, and executed as
/// one command. That is what the unit coverage cannot give: the common table expressions, the
/// <c>numeric</c> ceiling division, and the <c>bigint</c> modulo either run on the provider or they do
/// not, and the shape assertions cannot tell the difference.
/// <para>
/// Every case and every expected boundary comes from
/// <see cref="PartitionWindowProbeScenarios" />, which the SQL Server probe reads too, so the two
/// providers are held to identical typed ranges for identical data.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Compiled_Partition_Boundary_Statement
{
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSource _dataSource = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // A per-run database: the probe creates its own root relation and must never leave it where
        // another fixture or a later run can observe it.
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        _dataSource = NpgsqlDataSource.Create(_database.ConnectionString);

        await using var connection = await _dataSource.OpenConnectionAsync();

        foreach (var statement in PartitionWindowProbeScenarios.BuildPostgresqlSchemaStatements())
        {
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        // Both are guarded because one-time setup can fail between the two assignments; an unguarded
        // dispose would throw over the real setup failure and hide it.
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_should_seed_sparse_identifiers_so_a_boundary_cannot_be_arithmetic()
    {
        var seededCount = await ScalarAsync("""SELECT COUNT(*) FROM "edfi"."PartitionProbeRoot";""");
        var lowestId = await ScalarAsync("""SELECT MIN("DocumentId") FROM "edfi"."PartitionProbeRoot";""");
        var highestId = await ScalarAsync("""SELECT MAX("DocumentId") FROM "edfi"."PartitionProbeRoot";""");

        seededCount.Should().Be(PartitionWindowProbeScenarios.SeededRowCount);
        lowestId.Should().Be(PartitionWindowProbeScenarios.DocumentIdAt(1));
        highestId
            .Should()
            .Be(
                PartitionWindowProbeScenarios.DocumentIdAt(PartitionWindowProbeScenarios.SeededRowCount),
                "the identifier range must be far wider than the row count, or dividing the range arithmetically would coincide with selecting by row number"
            );
    }

    [Test]
    public async Task It_should_produce_the_shared_cross_provider_boundaries_for_every_scenario()
    {
        foreach (var scenario in PartitionWindowProbeScenarios.All)
        {
            var starts = await SelectPartitionStartsAsync(scenario);

            starts
                .Should()
                .Equal(
                    scenario.ExpectedStarts,
                    $"the compiled boundary statement must select these starting identifiers for {scenario.Description}"
                );
            starts
                .Should()
                .OnlyHaveUniqueItems(
                    $"a repeated start would hand out a duplicated partition for {scenario.Description}"
                );
        }
    }

    [Test]
    public async Task It_should_assemble_contiguous_non_overlapping_ranges_with_an_unbounded_final_range()
    {
        foreach (var scenario in PartitionWindowProbeScenarios.All)
        {
            var starts = await SelectPartitionStartsAsync(scenario);
            var ranges = PartitionRangeAssembler.ToInclusiveRanges(starts);

            ranges
                .Should()
                .Equal(
                    PartitionWindowProbeScenarios.ExpectedRanges(scenario),
                    $"executed starts must assemble into the shared expected ranges for {scenario.Description}"
                );

            if (ranges.Count == 0)
            {
                continue;
            }

            ranges[^1]
                .InclusiveMaximum.Should()
                .Be(
                    long.MaxValue,
                    $"the final partition must stay open so later inserts remain reachable for {scenario.Description}"
                );

            for (var index = 0; index + 1 < ranges.Count; index++)
            {
                ranges[index]
                    .InclusiveMaximum.Should()
                    .Be(
                        ranges[index + 1].InclusiveMinimum - 1,
                        $"partition {index} must close exactly one before the next begins for {scenario.Description}"
                    );
            }
        }
    }

    // Every start must be an identifier the filtered, windowed candidate set actually contains. A
    // boundary at an identifier the caller cannot reach would hand out a partition that returns nothing.
    [Test]
    public async Task It_should_only_return_identifiers_the_candidate_set_actually_contains()
    {
        foreach (var scenario in PartitionWindowProbeScenarios.All)
        {
            var starts = await SelectPartitionStartsAsync(scenario);
            var candidateIds = await SelectCandidateIdsAsync(scenario);

            starts
                .Should()
                .BeSubsetOf(
                    candidateIds,
                    $"every boundary must be a stored candidate identifier for {scenario.Description}"
                );
        }
    }

    [Test]
    public async Task It_should_execute_one_identifiers_only_statement()
    {
        var scenario = PartitionWindowProbeScenarios.All[1];
        var partitionPlan = PlanPartitionWindow(scenario);

        partitionPlan.Plan.TotalCountSql.Should().BeNull();
        partitionPlan.Plan.PageDocumentIdSql.TrimEnd().Should().EndWith(";");
        partitionPlan.Plan.PageDocumentIdSql.Count(character => character == ';').Should().Be(1);

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(partitionPlan.Plan.PageDocumentIdSql, connection);

        BindParameters(command, partitionPlan);

        await using var reader = await command.ExecuteReaderAsync();

        reader
            .FieldCount.Should()
            .Be(1, "the boundary statement returns starting identifiers and nothing else");

        (await reader.NextResultAsync())
            .Should()
            .BeFalse("the endpoint performs one command and returns no total count");
    }

    private static PartitionWindowPlan PlanPartitionWindow(PartitionWindowProbeScenario scenario)
    {
        var planned = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql).TryPlanCandidates(
            CandidateProbePlannerInputs.CreateRootTableModel(PartitionWindowProbeScenarios.RootTable),
            scenario.SchoolIdFilter is { } schoolId
                ? CandidateProbePlannerInputs.CreateRootSchoolIdFilter(schoolId)
                : PartitionWindowProbeScenarios.CreateUnfilteredPreprocessingResult(),
            out var candidatePlan,
            out var emptyPageReason,
            changeVersionRange: scenario.ChangeVersionRange
        );

        planned
            .Should()
            .BeTrue($"the candidate relation must plan for {scenario.Description}: {emptyPageReason}");

        return new PartitionWindowPlanner(SqlDialect.Pgsql).Plan(
            candidatePlan!,
            scenario.RequestedPartitionCount,
            scenario.MinimumPartitionSize
        );
    }

    private async Task<IReadOnlyList<long>> SelectPartitionStartsAsync(PartitionWindowProbeScenario scenario)
    {
        var partitionPlan = PlanPartitionWindow(scenario);

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(partitionPlan.Plan.PageDocumentIdSql, connection);

        BindParameters(command, partitionPlan);

        List<long> starts = [];

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            starts.Add(reader.GetInt64(0));
        }

        return starts;
    }

    /// <summary>
    /// The candidate identifiers the same filters and window select, read directly rather than through
    /// the boundary statement, so the subset assertion has an independent reference.
    /// </summary>
    private async Task<IReadOnlyList<long>> SelectCandidateIdsAsync(PartitionWindowProbeScenario scenario)
    {
        var sql = """SELECT "DocumentId" FROM "edfi"."PartitionProbeRoot" WHERE 1 = 1""";

        if (scenario.SchoolIdFilter is { } schoolId)
        {
            sql += $""" AND "SchoolId" = {schoolId}""";
        }

        if (scenario.ChangeVersionRange?.MinChangeVersion is { } minChangeVersion)
        {
            sql += $""" AND "ContentVersion" >= {minChangeVersion}""";
        }

        if (scenario.ChangeVersionRange?.MaxChangeVersion is { } maxChangeVersion)
        {
            sql += $""" AND "ContentVersion" <= {maxChangeVersion}""";
        }

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"{sql} ORDER BY \"DocumentId\";", connection);

        List<long> candidateIds = [];

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            candidateIds.Add(reader.GetInt64(0));
        }

        return candidateIds;
    }

    private static void BindParameters(NpgsqlCommand command, PartitionWindowPlan partitionPlan)
    {
        foreach (QuerySqlParameter parameter in partitionPlan.Plan.PageParametersInOrder)
        {
            command.Parameters.AddWithValue(
                parameter.ParameterName,
                partitionPlan.ParameterValues[parameter.ParameterName]!
            );
        }
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }
}
