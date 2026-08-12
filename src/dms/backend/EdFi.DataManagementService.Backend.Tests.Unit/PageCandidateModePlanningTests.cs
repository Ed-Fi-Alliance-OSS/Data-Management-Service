// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The planned candidate mode's bound values are what callers spend against SQL Server's per-command
/// parameter budget before any SQL exists, so they have to match the parameters the compiled plan
/// actually binds. A mode that binds more parameters than its planned values report would let a query
/// pass the budget check and then fail at execution instead of failing closed.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_PageCandidateModePlanning
{
    private static readonly DbTableName _rootTable = new(new DbSchemaName("edfi"), "School");

    [Test]
    public void It_should_bind_an_offset_and_a_limit_for_traditional_paging()
    {
        var planned = PageCandidateModePlanning.ForPaging(
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 75, TotalCount: false, MaximumPageSize: 500)
            ),
            PageOrderingMode.DocumentId
        );

        planned.ParameterValues.Should().HaveCount(2);
        PlannedParameterNames(planned).Should().Equal("offset", "limit");
    }

    [Test]
    public void It_should_bind_two_bounds_and_a_page_size_for_cursor_paging()
    {
        var planned = PageCandidateModePlanning.ForPaging(
            new CollectionPaging.Cursor(new CursorRange(1L, 100L), new PageSize(25)),
            PageOrderingMode.DocumentId
        );

        planned.ParameterValues.Should().HaveCount(3);
        PlannedParameterNames(planned).Should().Equal("cursorMin", "cursorMax", "pageSize");
    }

    [Test]
    public void It_should_bind_nothing_for_the_unpaged_candidate_relation()
    {
        var planned = PageCandidateModePlanning.ForUnpagedCandidates();

        planned.ParameterValues.Should().BeEmpty();
        planned.OwnedParameterNames.Should().Equal("number", "minimumPartitionSize");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_plan_exactly_the_mode_parameters_the_compiled_plan_binds(SqlDialect dialect)
    {
        PlannedCandidateMode[] plannedModes =
        [
            PageCandidateModePlanning.ForPaging(
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 75, TotalCount: false, MaximumPageSize: 500)
                ),
                PageOrderingMode.DocumentId
            ),
            PageCandidateModePlanning.ForPaging(
                new CollectionPaging.Cursor(new CursorRange(1L, 100L), new PageSize(25)),
                PageOrderingMode.DocumentId
            ),
            PageCandidateModePlanning.ForUnpagedCandidates(),
        ];
        var compiler = new PageDocumentIdSqlCompiler(dialect);

        foreach (var planned in plannedModes)
        {
            var plan = compiler.Compile(
                new PageDocumentIdQuerySpec(
                    _rootTable,
                    [],
                    new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
                    planned.Mode
                )
            );
            var boundModeParameterNames = plan
                .PageParametersInOrder.Where(static parameter =>
                    parameter.Role is not QuerySqlParameterRole.Filter
                )
                .Select(static parameter => parameter.ParameterName);

            PlannedParameterNames(planned).Should().Equal(boundModeParameterNames);
        }
    }

    private static IEnumerable<string> PlannedParameterNames(PlannedCandidateMode planned)
    {
        return planned.ParameterValues.Select(static parameterValue => parameterValue.Key);
    }
}
