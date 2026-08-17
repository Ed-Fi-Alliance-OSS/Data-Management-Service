// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Real-SQL Server execution evidence for the compiled partition-boundary statement.
/// </summary>
/// <remarks>
/// SQL Server is where the dialect-specific parts of the statement have to be proven: <c>COUNT_BIG</c>
/// over the window, the <c>CASE</c> that stands in for <c>GREATEST</c>, and the <c>decimal</c> ceiling
/// cast back to <c>bigint</c> so the modulo operands match <c>ROW_NUMBER()</c>. None of that can be
/// established by inspecting emitted text.
/// <para>
/// Every case and every expected boundary comes from
/// <see cref="PartitionWindowProbeScenarios" />, which the PostgreSQL probe reads too, so the two
/// providers are held to identical typed ranges for identical data.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_Compiled_Partition_Boundary_Statement
{
    private string _connectionString = null!;
    private string _databaseName = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server partition boundary probes require a MssqlAdmin connection string"
        );

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        foreach (var statement in PartitionWindowProbeScenarios.BuildMssqlSchemaStatements())
        {
            await using SqlCommand command = new(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null)
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public async Task It_should_seed_sparse_identifiers_so_a_boundary_cannot_be_arithmetic()
    {
        var seededCount = await ScalarAsync("SELECT COUNT_BIG(*) FROM [edfi].[PartitionProbeRoot];");
        var lowestId = await ScalarAsync("SELECT MIN([DocumentId]) FROM [edfi].[PartitionProbeRoot];");
        var highestId = await ScalarAsync("SELECT MAX([DocumentId]) FROM [edfi].[PartitionProbeRoot];");

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

        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(partitionPlan.Plan.PageDocumentIdSql, connection);

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
        var planned = new RelationalQueryPageKeysetPlanner(SqlDialect.Mssql).TryPlanCandidates(
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

        return new PartitionWindowPlanner(SqlDialect.Mssql).Plan(
            candidatePlan!,
            scenario.RequestedPartitionCount,
            scenario.MinimumPartitionSize
        );
    }

    private async Task<IReadOnlyList<long>> SelectPartitionStartsAsync(PartitionWindowProbeScenario scenario)
    {
        var partitionPlan = PlanPartitionWindow(scenario);

        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(partitionPlan.Plan.PageDocumentIdSql, connection);

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
        var sql = "SELECT [DocumentId] FROM [edfi].[PartitionProbeRoot] WHERE 1 = 1";

        if (scenario.SchoolIdFilter is { } schoolId)
        {
            sql += $" AND [SchoolId] = {schoolId}";
        }

        if (scenario.ChangeVersionRange?.MinChangeVersion is { } minChangeVersion)
        {
            sql += $" AND [ContentVersion] >= {minChangeVersion}";
        }

        if (scenario.ChangeVersionRange?.MaxChangeVersion is { } maxChangeVersion)
        {
            sql += $" AND [ContentVersion] <= {maxChangeVersion}";
        }

        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new($"{sql} ORDER BY [DocumentId];", connection);

        List<long> candidateIds = [];

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            candidateIds.Add(reader.GetInt64(0));
        }

        return candidateIds;
    }

    private static void BindParameters(SqlCommand command, PartitionWindowPlan partitionPlan)
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
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection);

        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
