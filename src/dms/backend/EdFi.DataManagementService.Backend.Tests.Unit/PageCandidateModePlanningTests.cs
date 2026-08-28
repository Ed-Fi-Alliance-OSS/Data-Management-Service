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
        var planned = PageCandidateModePlanning.ForUnpagedCandidates(PageOrderingMode.DocumentId);

        planned.ParameterValues.Should().BeEmpty();
        planned.OwnedParameterNames.Should().Equal("number", "minimumPartitionSize");
    }

    /// <summary>
    /// The resolved anchor reaches the cursor mode instead of being discarded. Asserted with
    /// ContentVersion because DocumentId is the enum's zero value: a discarded anchor would still leave
    /// a DocumentId mode behind and read as though it had been applied.
    /// </summary>
    [Test]
    public void It_should_carry_the_resolved_anchor_onto_a_cursor_mode()
    {
        var planned = PageCandidateModePlanning.ForPaging(
            new CollectionPaging.Cursor(new CursorRange(1L, 100L), new PageSize(25)),
            PageOrderingMode.ContentVersion
        );

        planned
            .Mode.Should()
            .BeOfType<PageCandidateMode.Cursor>()
            .Which.OrderingMode.Should()
            .Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_should_carry_the_resolved_anchor_onto_a_traditional_mode()
    {
        var planned = PageCandidateModePlanning.ForPaging(
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 75, TotalCount: false, MaximumPageSize: 500)
            ),
            PageOrderingMode.ContentVersion
        );

        planned
            .Mode.Should()
            .BeOfType<PageCandidateMode.Traditional>()
            .Which.OrderingMode.Should()
            .Be(PageOrderingMode.ContentVersion);
    }

    [Test]
    public void It_should_carry_the_resolved_anchor_onto_the_unpaged_candidate_relation()
    {
        var planned = PageCandidateModePlanning.ForUnpagedCandidates(PageOrderingMode.ContentVersion);

        planned
            .Mode.Should()
            .BeOfType<PageCandidateMode.UnpagedCandidates>()
            .Which.OrderingMode.Should()
            .Be(PageOrderingMode.ContentVersion);
    }

    /// <summary>
    /// The anchor changes which column a mode is ordered and bounded on, never which parameters it
    /// owns. Filter-name allocation reserves the owned names, so an anchor that moved them would suffix
    /// a filter parameter over a collision the query does not have and shift SQL that has no stake in
    /// the anchor at all.
    /// </summary>
    [TestCase(PageOrderingMode.DocumentId)]
    [TestCase(PageOrderingMode.ContentVersion)]
    public void It_should_own_the_same_parameter_names_under_either_anchor(PageOrderingMode orderingMode)
    {
        PageCandidateModePlanning
            .ForPaging(
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 75, TotalCount: false, MaximumPageSize: 500)
                ),
                orderingMode
            )
            .OwnedParameterNames.Should()
            .Equal("offset", "limit");

        PageCandidateModePlanning
            .ForPaging(new CollectionPaging.Cursor(new CursorRange(1L, 100L), new PageSize(25)), orderingMode)
            .OwnedParameterNames.Should()
            .Equal("cursorMin", "cursorMax", "pageSize");

        PageCandidateModePlanning
            .ForUnpagedCandidates(orderingMode)
            .OwnedParameterNames.Should()
            .Equal("number", "minimumPartitionSize");
    }

    /// <summary>
    /// Run under both anchors, because the parameter budget is spent from the planned values before any
    /// SQL exists: if an anchor changed what the compiled plan binds, a windowed query would pass the
    /// budget check and then fail at execution rather than failing closed.
    /// </summary>
    [TestCase(SqlDialect.Pgsql, PageOrderingMode.DocumentId)]
    [TestCase(SqlDialect.Pgsql, PageOrderingMode.ContentVersion)]
    [TestCase(SqlDialect.Mssql, PageOrderingMode.DocumentId)]
    [TestCase(SqlDialect.Mssql, PageOrderingMode.ContentVersion)]
    public void It_should_plan_exactly_the_mode_parameters_the_compiled_plan_binds(
        SqlDialect dialect,
        PageOrderingMode orderingMode
    )
    {
        PlannedCandidateMode[] plannedModes =
        [
            PageCandidateModePlanning.ForPaging(
                new CollectionPaging.Traditional(
                    new PaginationParameters(Limit: 25, Offset: 75, TotalCount: false, MaximumPageSize: 500)
                ),
                orderingMode
            ),
            PageCandidateModePlanning.ForPaging(
                new CollectionPaging.Cursor(new CursorRange(1L, 100L), new PageSize(25)),
                orderingMode
            ),
            PageCandidateModePlanning.ForUnpagedCandidates(orderingMode),
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
