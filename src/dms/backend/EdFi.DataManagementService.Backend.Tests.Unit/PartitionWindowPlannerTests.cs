// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Pairing an already-compiled candidate relation with the two values the boundary statement binds.
/// </summary>
/// <remarks>
/// The candidate relation comes from the real page keyset planner rather than a hand-built plan, so
/// these prove the planner's own reserved names are the names the boundary statement binds. A
/// hand-built plan could agree with the compiler while both disagreed with what a request reserves.
/// </remarks>
[TestFixture]
[Parallelizable]
public class Given_A_Partition_Window_Planner
{
    private static readonly DbTableName _rootTable = new(new DbSchemaName("edfi"), "CandidateProbeRoot");
    private const long SchoolIdFilterValue = 900L;

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_binds_the_requested_count_and_minimum_size_under_the_reserved_names(SqlDialect dialect)
    {
        var plan = new PartitionWindowPlanner(dialect).Plan(
            PlanCandidates(dialect),
            requestedPartitionCount: 12,
            minimumPartitionSize: 750L
        );

        plan.ParameterValues["number"].Should().Be(12L);
        plan.ParameterValues["minimumPartitionSize"].Should().Be(750L);
    }

    // Both bind as Int64 so the statement's division and modulo stay in the width ROW_NUMBER() produces.
    // An Int32 count would leave the division operand width for the provider to infer.
    [Test]
    public void It_binds_both_partition_values_as_int64()
    {
        var plan = new PartitionWindowPlanner(SqlDialect.Pgsql).Plan(
            PlanCandidates(SqlDialect.Pgsql),
            requestedPartitionCount: 3,
            minimumPartitionSize: 500L
        );

        plan.ParameterValues["number"].Should().BeOfType<long>();
        plan.ParameterValues["minimumPartitionSize"].Should().BeOfType<long>();
    }

    [Test]
    public void It_preserves_every_value_the_candidate_relation_already_bound()
    {
        var candidatePlan = PlanCandidates(SqlDialect.Pgsql);

        var plan = new PartitionWindowPlanner(SqlDialect.Pgsql).Plan(
            candidatePlan,
            requestedPartitionCount: 4,
            minimumPartitionSize: 500L
        );

        foreach (var (parameterName, parameterValue) in candidatePlan.ParameterValues)
        {
            plan.ParameterValues.Should().ContainKey(parameterName);
            plan.ParameterValues[parameterName].Should().Be(parameterValue);
        }

        plan.ParameterValues.Should().HaveCount(candidatePlan.ParameterValues.Count + 2);
    }

    // Every parameter the compiled statement inventories must have a value, or the shared binder fails
    // the command at execution rather than at planning.
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_supplies_a_value_for_every_parameter_the_compiled_statement_inventories(SqlDialect dialect)
    {
        var plan = new PartitionWindowPlanner(dialect).Plan(
            PlanCandidates(dialect),
            requestedPartitionCount: 4,
            minimumPartitionSize: 500L
        );

        plan.Plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .OnlyContain(parameterName => plan.ParameterValues.ContainsKey(parameterName));
        plan.Plan.PageParametersInOrder.Select(parameter => parameter.Role)
            .Should()
            .EndWith([QuerySqlParameterRole.PartitionCount, QuerySqlParameterRole.MinimumPartitionSize]);
        plan.Plan.TotalCountSql.Should().BeNull();
    }

    // Both values divide or clamp inside the statement. A zero count divides by zero at the provider and
    // a non-positive minimum makes every candidate row a boundary, so neither may reach the SQL.
    [TestCase(0)]
    [TestCase(-1)]
    public void It_rejects_a_requested_partition_count_below_one(int requestedPartitionCount)
    {
        var action = () =>
            new PartitionWindowPlanner(SqlDialect.Pgsql).Plan(
                PlanCandidates(SqlDialect.Pgsql),
                requestedPartitionCount,
                minimumPartitionSize: 500L
            );

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestCase(0L)]
    [TestCase(-1L)]
    public void It_rejects_a_minimum_partition_size_below_one(long minimumPartitionSize)
    {
        var action = () =>
            new PartitionWindowPlanner(SqlDialect.Pgsql).Plan(
                PlanCandidates(SqlDialect.Pgsql),
                requestedPartitionCount: 4,
                minimumPartitionSize
            );

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void It_rejects_a_null_candidate_plan()
    {
        var action = () =>
            new PartitionWindowPlanner(SqlDialect.Pgsql).Plan(
                null!,
                requestedPartitionCount: 4,
                minimumPartitionSize: 500L
            );

        action.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// The unpaged candidate relation the real regular-resource planner produces for a root-column
    /// filter, so the reserved names under test are the ones a request actually reserves.
    /// </summary>
    private static CandidateQueryPlan PlanCandidates(SqlDialect dialect)
    {
        var planned = new RelationalQueryPageKeysetPlanner(dialect).TryPlanCandidates(
            CandidateProbePlannerInputs.CreateRootTableModel(_rootTable),
            CandidateProbePlannerInputs.CreateRootSchoolIdFilter(SchoolIdFilterValue),
            out var candidatePlan,
            out var emptyPageReason
        );

        planned.Should().BeTrue(emptyPageReason);

        return candidatePlan!;
    }
}
