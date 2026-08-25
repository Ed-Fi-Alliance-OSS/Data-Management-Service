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

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_PageDocumentIdSqlCompiler_In_Cursor_Mode
{
    private PageDocumentIdSqlPlan _pgsqlPlan = null!;
    private PageDocumentIdSqlPlan _mssqlPlan = null!;

    [SetUp]
    public void Setup()
    {
        _pgsqlPlan = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql).Compile(
            CandidateModeTestSpecs.CreateSpec(new PageCandidateMode.Cursor())
        );
        _mssqlPlan = new PageDocumentIdSqlCompiler(SqlDialect.Mssql).Compile(
            CandidateModeTestSpecs.CreateSpec(new PageCandidateMode.Cursor())
        );
    }

    [Test]
    public void It_should_emit_both_inclusive_document_id_bounds_for_pgsql()
    {
        _pgsqlPlan.PageDocumentIdSql.Should().Contain("AND (r.\"DocumentId\" >= @cursorMin)");
        _pgsqlPlan.PageDocumentIdSql.Should().Contain("AND (r.\"DocumentId\" <= @cursorMax)");
    }

    [Test]
    public void It_should_emit_both_inclusive_document_id_bounds_for_mssql()
    {
        _mssqlPlan.PageDocumentIdSql.Should().Contain("AND (r.[DocumentId] >= @cursorMin)");
        _mssqlPlan.PageDocumentIdSql.Should().Contain("AND (r.[DocumentId] <= @cursorMax)");
    }

    [Test]
    public void It_should_order_by_ascending_document_id_for_both_dialects()
    {
        _pgsqlPlan.PageDocumentIdSql.Should().Contain("ORDER BY r.\"DocumentId\" ASC");
        _mssqlPlan.PageDocumentIdSql.Should().Contain("ORDER BY r.[DocumentId] ASC");
    }

    [Test]
    public void It_should_limit_the_pgsql_page_with_a_trailing_limit_clause()
    {
        _pgsqlPlan.PageDocumentIdSql.Should().Contain("LIMIT @pageSize");
        _pgsqlPlan.PageDocumentIdSql.Should().NotContain("TOP (");
    }

    [Test]
    public void It_should_limit_the_mssql_page_with_a_select_list_top_clause()
    {
        _mssqlPlan.PageDocumentIdSql.Should().StartWith("SELECT TOP (@pageSize) r.[DocumentId]");
        _mssqlPlan.PageDocumentIdSql.Should().NotContain("FETCH NEXT");
    }

    [Test]
    public void It_should_not_emit_any_offset_operation()
    {
        _pgsqlPlan.PageDocumentIdSql.Should().NotContain("OFFSET");
        _mssqlPlan.PageDocumentIdSql.Should().NotContain("OFFSET");
    }

    [Test]
    public void It_should_not_emit_a_row_number_skip()
    {
        _pgsqlPlan.PageDocumentIdSql.Should().NotContain("ROW_NUMBER");
        _mssqlPlan.PageDocumentIdSql.Should().NotContain("ROW_NUMBER");
    }

    [Test]
    public void It_should_not_emit_total_count_work()
    {
        _pgsqlPlan.PageDocumentIdSql.Should().NotContain("COUNT");
        _mssqlPlan.PageDocumentIdSql.Should().NotContain("COUNT");
        _pgsqlPlan.TotalCountSql.Should().BeNull();
        _mssqlPlan.TotalCountSql.Should().BeNull();
        _pgsqlPlan.TotalCountParametersInOrder.Should().BeNull();
        _mssqlPlan.TotalCountParametersInOrder.Should().BeNull();
    }

    [Test]
    public void It_should_not_emit_distinct()
    {
        _pgsqlPlan.PageDocumentIdSql.Should().NotContain("DISTINCT");
        _mssqlPlan.PageDocumentIdSql.Should().NotContain("DISTINCT");
    }

    [Test]
    public void It_should_inventory_filter_parameters_then_the_three_cursor_roles_in_canonical_order()
    {
        _pgsqlPlan
            .PageParametersInOrder.Select(parameter => (parameter.Role, parameter.ParameterName))
            .Should()
            .Equal(
                (QuerySqlParameterRole.Filter, "schoolYear"),
                (QuerySqlParameterRole.CursorInclusiveMinimum, "cursorMin"),
                (QuerySqlParameterRole.CursorInclusiveMaximum, "cursorMax"),
                (QuerySqlParameterRole.PageSize, "pageSize")
            );
    }

    [Test]
    public void It_should_not_inventory_traditional_paging_roles()
    {
        _pgsqlPlan
            .PageParametersInOrder.Select(parameter => parameter.Role)
            .Should()
            .NotContain(QuerySqlParameterRole.Offset)
            .And.NotContain(QuerySqlParameterRole.Limit);
    }
}

[TestFixture]
public class Given_PageDocumentIdSqlCompiler_In_Unpaged_Candidates_Mode
{
    private PageDocumentIdSqlPlan _pgsqlPlan = null!;
    private PageDocumentIdSqlPlan _mssqlPlan = null!;

    [SetUp]
    public void Setup()
    {
        _pgsqlPlan = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql).Compile(
            CandidateModeTestSpecs.CreateSpec(new PageCandidateMode.UnpagedCandidates())
        );
        _mssqlPlan = new PageDocumentIdSqlCompiler(SqlDialect.Mssql).Compile(
            CandidateModeTestSpecs.CreateSpec(new PageCandidateMode.UnpagedCandidates())
        );
    }

    [Test]
    public void It_should_not_emit_an_order_by_clause()
    {
        // The consumer wraps this relation in a CTE and applies its own row numbering. SQL Server
        // rejects ORDER BY in a CTE that has no TOP or OFFSET, so emitting one would make the
        // partition query fail to parse on that provider.
        _pgsqlPlan.PageDocumentIdSql.Should().NotContain("ORDER BY");
        _mssqlPlan.PageDocumentIdSql.Should().NotContain("ORDER BY");
    }

    [Test]
    public void It_should_not_emit_any_size_or_offset_clause()
    {
        _pgsqlPlan.PageDocumentIdSql.Should().NotContain("LIMIT");
        _pgsqlPlan.PageDocumentIdSql.Should().NotContain("OFFSET");
        _mssqlPlan.PageDocumentIdSql.Should().NotContain("TOP (");
        _mssqlPlan.PageDocumentIdSql.Should().NotContain("OFFSET");
    }

    [Test]
    public void It_should_not_emit_total_count_work()
    {
        _pgsqlPlan.TotalCountSql.Should().BeNull();
        _mssqlPlan.TotalCountSql.Should().BeNull();
    }

    [Test]
    public void It_should_inventory_filter_parameters_only()
    {
        _pgsqlPlan
            .PageParametersInOrder.Select(parameter => (parameter.Role, parameter.ParameterName))
            .Should()
            .Equal((QuerySqlParameterRole.Filter, "schoolYear"));
    }

    [Test]
    public void It_should_not_inventory_the_reserved_partition_roles()
    {
        // Reserved names are collision-validated but never inventoried: an inventory entry with no
        // placeholder in the SQL would fail runtime parameter binding.
        _pgsqlPlan
            .PageParametersInOrder.Select(parameter => parameter.Role)
            .Should()
            .NotContain(QuerySqlParameterRole.PartitionCount)
            .And.NotContain(QuerySqlParameterRole.MinimumPartitionSize);
    }
}

/// <summary>
/// A cursor page whose request resolved a ContentVersion anchor. Bounds, ordering, and projection all
/// follow the anchor; the page-size clause and everything the filter and authorization fragments emit
/// are untouched by it.
/// </summary>
[TestFixture]
public class Given_PageDocumentIdSqlCompiler_In_Content_Version_Anchored_Cursor_Mode
{
    private static PageDocumentIdSqlPlan Compile(SqlDialect dialect) =>
        new PageDocumentIdSqlCompiler(dialect).Compile(
            CandidateModeTestSpecs.CreateSpec(
                new PageCandidateMode.Cursor(OrderingMode: PageOrderingMode.ContentVersion)
            )
        );

    [Test]
    public void It_should_bound_the_page_on_the_content_version_for_pgsql()
    {
        var sql = Compile(SqlDialect.Pgsql).PageDocumentIdSql;

        sql.Should().Contain("AND (r.\"ContentVersion\" >= @cursorMin)");
        sql.Should().Contain("AND (r.\"ContentVersion\" <= @cursorMax)");
        sql.Should().NotContain("\"DocumentId\" >=");
        sql.Should().NotContain("\"DocumentId\" <=");
    }

    [Test]
    public void It_should_bound_the_page_on_the_content_version_for_mssql()
    {
        var sql = Compile(SqlDialect.Mssql).PageDocumentIdSql;

        sql.Should().Contain("AND (r.[ContentVersion] >= @cursorMin)");
        sql.Should().Contain("AND (r.[ContentVersion] <= @cursorMax)");
        sql.Should().NotContain("[DocumentId] >=");
        sql.Should().NotContain("[DocumentId] <=");
    }

    /// <summary>
    /// The bounds stay inclusive under this anchor. An exclusive lower bound would be the same
    /// predicate over an integer sequence, and one bound shape keeps one token shape.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_keep_the_inclusive_bound_operators(SqlDialect dialect)
    {
        var sql = Compile(dialect).PageDocumentIdSql;

        sql.Should().NotContain(" > @cursorMin");
        sql.Should().NotContain(" < @cursorMax");
    }

    [Test]
    public void It_should_order_by_ascending_content_version_for_both_dialects()
    {
        Compile(SqlDialect.Pgsql)
            .PageDocumentIdSql.Should()
            .Contain("ORDER BY r.\"ContentVersion\" ASC")
            .And.NotContain("ORDER BY r.\"DocumentId\"");
        Compile(SqlDialect.Mssql)
            .PageDocumentIdSql.Should()
            .Contain("ORDER BY r.[ContentVersion] ASC")
            .And.NotContain("ORDER BY r.[DocumentId]");
    }

    /// <summary>
    /// The page projects both columns, and neither is optional. DocumentId feeds the keyset insert and
    /// every downstream hydration join; ContentVersion is the continuation anchor, and hydration can
    /// only read columns this embedded SQL projects. Dropping the anchor here would compile and then
    /// force a second lookup after selection to recover it, which is the concurrent-delete stall the
    /// design forbids.
    /// </summary>
    [Test]
    public void It_should_project_the_document_id_and_the_anchor_for_pgsql()
    {
        Compile(SqlDialect.Pgsql)
            .PageDocumentIdSql.Should()
            .StartWith("SELECT r.\"DocumentId\", r.\"ContentVersion\"\n");
    }

    [Test]
    public void It_should_project_the_document_id_and_the_anchor_after_the_top_clause_for_mssql()
    {
        Compile(SqlDialect.Mssql)
            .PageDocumentIdSql.Should()
            .StartWith("SELECT TOP (@pageSize) r.[DocumentId], r.[ContentVersion]\n");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_not_emit_an_offset_or_total_count(SqlDialect dialect)
    {
        var plan = Compile(dialect);

        plan.PageDocumentIdSql.Should().NotContain("OFFSET");
        plan.PageDocumentIdSql.Should().NotContain("COUNT");
        plan.TotalCountSql.Should().BeNull();
        plan.TotalCountParametersInOrder.Should().BeNull();
    }

    [Test]
    public void It_should_size_the_page_the_same_way_either_anchor_does()
    {
        Compile(SqlDialect.Pgsql).PageDocumentIdSql.Should().Contain("LIMIT @pageSize");
        Compile(SqlDialect.Mssql).PageDocumentIdSql.Should().Contain("TOP (@pageSize)");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_inventory_the_same_parameter_roles_either_anchor_does(SqlDialect dialect)
    {
        Compile(dialect)
            .PageParametersInOrder.Select(parameter => (parameter.Role, parameter.ParameterName))
            .Should()
            .Equal(
                (QuerySqlParameterRole.Filter, "schoolYear"),
                (QuerySqlParameterRole.CursorInclusiveMinimum, "cursorMin"),
                (QuerySqlParameterRole.CursorInclusiveMaximum, "cursorMax"),
                (QuerySqlParameterRole.PageSize, "pageSize")
            );
    }
}

/// <summary>
/// The partition candidate relation under a ContentVersion anchor. It projects the anchor alone,
/// because the consumer ranks and cuts boundaries on that column and hands the value back.
/// </summary>
[TestFixture]
public class Given_PageDocumentIdSqlCompiler_In_Content_Version_Anchored_Unpaged_Candidates_Mode
{
    private static PageDocumentIdSqlPlan Compile(SqlDialect dialect) =>
        new PageDocumentIdSqlCompiler(dialect).Compile(
            CandidateModeTestSpecs.CreateSpec(
                new PageCandidateMode.UnpagedCandidates(OrderingMode: PageOrderingMode.ContentVersion)
            )
        );

    [Test]
    public void It_should_project_the_anchor_alone_for_pgsql()
    {
        Compile(SqlDialect.Pgsql)
            .PageDocumentIdSql.Should()
            .StartWith("SELECT r.\"ContentVersion\"\n")
            .And.NotContain("\"DocumentId\"");
    }

    [Test]
    public void It_should_project_the_anchor_alone_for_mssql()
    {
        Compile(SqlDialect.Mssql)
            .PageDocumentIdSql.Should()
            .StartWith("SELECT r.[ContentVersion]\n")
            .And.NotContain("[DocumentId]");
    }

    /// <summary>
    /// Still unordered: the consumer wraps this relation in a CTE and applies its own row numbering,
    /// and SQL Server rejects ORDER BY in a CTE with no TOP or OFFSET. The anchor names the column that
    /// consumer ranks, not an ORDER BY here.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_not_emit_an_order_by_or_a_size_clause(SqlDialect dialect)
    {
        var sql = Compile(dialect).PageDocumentIdSql;

        sql.Should().NotContain("ORDER BY");
        sql.Should().NotContain("LIMIT");
        sql.Should().NotContain("TOP (");
        sql.Should().NotContain("OFFSET");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_inventory_filter_parameters_only(SqlDialect dialect)
    {
        Compile(dialect)
            .PageParametersInOrder.Select(parameter => (parameter.Role, parameter.ParameterName))
            .Should()
            .Equal((QuerySqlParameterRole.Filter, "schoolYear"));
    }
}

[TestFixture]
public class Given_PageDocumentIdSqlCompiler_Candidate_Mode_Parameter_Validation
{
    private static readonly PageDocumentIdSqlCompiler _compiler = new(SqlDialect.Pgsql);

    [TestCase("cursorMin")]
    [TestCase("CURSORMAX")]
    [TestCase("pageSize")]
    public void It_should_reject_filter_parameter_names_that_collide_with_cursor_parameter_names(
        string filterParameterName
    )
    {
        var act = () =>
            _compiler.Compile(
                CandidateModeTestSpecs.CreateSpec(
                    new PageCandidateMode.Cursor(),
                    filterParameterName: filterParameterName
                )
            );

        act.Should().Throw<ArgumentException>().WithParameterName("Predicates");
    }

    /// <summary>
    /// Collision detection reads the mode's parameter names, which the anchor does not change, so a
    /// filter named after a cursor bound is rejected under either anchor. Worth pinning separately:
    /// the anchor moved the column those parameters are compared against, and a reader could
    /// reasonably expect it to have moved the names too.
    /// </summary>
    [TestCase("cursorMin")]
    [TestCase("CURSORMAX")]
    [TestCase("pageSize")]
    public void It_should_reject_the_same_filter_collisions_under_a_content_version_anchor(
        string filterParameterName
    )
    {
        var act = () =>
            _compiler.Compile(
                CandidateModeTestSpecs.CreateSpec(
                    new PageCandidateMode.Cursor(OrderingMode: PageOrderingMode.ContentVersion),
                    filterParameterName: filterParameterName
                )
            );

        act.Should().Throw<ArgumentException>().WithParameterName("Predicates");
    }

    [TestCase("number")]
    [TestCase("MINIMUMPARTITIONSIZE")]
    public void It_should_reject_filter_parameter_names_that_collide_with_reserved_partition_names(
        string filterParameterName
    )
    {
        // Reserving these now means a resource filter cannot shadow the partition window's parameters
        // later, when partition SQL starts binding them.
        var act = () =>
            _compiler.Compile(
                CandidateModeTestSpecs.CreateSpec(
                    new PageCandidateMode.UnpagedCandidates(),
                    filterParameterName: filterParameterName
                )
            );

        act.Should().Throw<ArgumentException>().WithParameterName("Predicates");
    }

    [Test]
    public void It_should_reject_cursor_parameter_names_that_collide_with_each_other()
    {
        var act = () =>
            _compiler.Compile(
                CandidateModeTestSpecs.CreateSpec(
                    new PageCandidateMode.Cursor(
                        InclusiveMinimumParameterName: "bound",
                        InclusiveMaximumParameterName: "BOUND"
                    )
                )
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithParameterName("Mode")
            .WithMessage("Candidate mode parameter names must be distinct (case-insensitive).*");
    }

    [Test]
    public void It_should_reject_cursor_parameter_names_that_are_not_safe_to_emit()
    {
        var act = () =>
            _compiler.Compile(
                CandidateModeTestSpecs.CreateSpec(
                    new PageCandidateMode.Cursor(PageSizeParameterName: "1; DROP TABLE foo--")
                )
            );

        act.Should().Throw<ArgumentException>().WithParameterName("PageSizeParameterName");
    }
}

[TestFixture]
public class Given_PageDocumentIdSqlCompiler_Compiling_The_Same_Spec_In_Every_Candidate_Mode
{
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_produce_an_identical_shared_candidate_region(SqlDialect dialect)
    {
        var (traditional, cursor, unpaged) = CompileEveryMode(dialect);

        var traditionalRegion = CandidateSqlRegions.SharedCandidateRegion(
            traditional.PageDocumentIdSql,
            new PageCandidateMode.Traditional(),
            dialect
        );
        var cursorRegion = CandidateSqlRegions.SharedCandidateRegion(
            cursor.PageDocumentIdSql,
            new PageCandidateMode.Cursor(),
            dialect
        );
        var unpagedRegion = CandidateSqlRegions.SharedCandidateRegion(
            unpaged.PageDocumentIdSql,
            new PageCandidateMode.UnpagedCandidates(),
            dialect
        );

        cursorRegion.Should().Be(traditionalRegion);
        unpagedRegion.Should().Be(traditionalRegion);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_produce_an_identical_authorization_fragment(SqlDialect dialect)
    {
        var (traditional, cursor, unpaged) = CompileEveryMode(dialect, withAuthorization: true);

        var traditionalRegion = CandidateSqlRegions.SharedCandidateRegion(
            traditional.PageDocumentIdSql,
            new PageCandidateMode.Traditional(),
            dialect
        );

        traditionalRegion.Should().Contain("Namespace");
        CandidateSqlRegions
            .SharedCandidateRegion(cursor.PageDocumentIdSql, new PageCandidateMode.Cursor(), dialect)
            .Should()
            .Be(traditionalRegion);
        CandidateSqlRegions
            .SharedCandidateRegion(
                unpaged.PageDocumentIdSql,
                new PageCandidateMode.UnpagedCandidates(),
                dialect
            )
            .Should()
            .Be(traditionalRegion);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_produce_identical_filter_parameters(SqlDialect dialect)
    {
        var (traditional, cursor, unpaged) = CompileEveryMode(dialect, withAuthorization: true);

        var traditionalFilters = CandidateSqlRegions.FilterParameters(traditional);

        CandidateSqlRegions.FilterParameters(cursor).Should().Equal(traditionalFilters);
        CandidateSqlRegions.FilterParameters(unpaged).Should().Equal(traditionalFilters);
    }

    private static (
        PageDocumentIdSqlPlan Traditional,
        PageDocumentIdSqlPlan Cursor,
        PageDocumentIdSqlPlan Unpaged
    ) CompileEveryMode(SqlDialect dialect, bool withAuthorization = false)
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var authorization = withAuthorization
            ? CandidateModeTestSpecs.CreateNamespaceAuthorization(dialect)
            : null;

        return (
            compiler.Compile(
                CandidateModeTestSpecs.CreateSpec(
                    new PageCandidateMode.Traditional(),
                    authorization: authorization
                )
            ),
            compiler.Compile(
                CandidateModeTestSpecs.CreateSpec(
                    new PageCandidateMode.Cursor(),
                    authorization: authorization
                )
            ),
            compiler.Compile(
                CandidateModeTestSpecs.CreateSpec(
                    new PageCandidateMode.UnpagedCandidates(),
                    authorization: authorization
                )
            )
        );
    }
}

/// <summary>
/// The anchor moves the range, ordering, and projection clauses and nothing else. The candidate root,
/// joins, filter predicates, and authorization fragment a DocumentId-anchored plan emits have to come
/// out byte-identical under a ContentVersion anchor, which is what makes an anchored page selectable
/// from the same authorized candidate relation an unanchored one is.
/// </summary>
[TestFixture]
public class Given_PageDocumentIdSqlCompiler_Compiling_One_Spec_Under_Both_Anchors
{
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_produce_an_identical_shared_candidate_region_for_a_cursor_page(SqlDialect dialect)
    {
        AssertSharedRegionsMatch(
            dialect,
            new PageCandidateMode.Cursor(),
            new PageCandidateMode.Cursor(OrderingMode: PageOrderingMode.ContentVersion),
            withAuthorization: false
        );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_produce_an_identical_authorization_fragment_for_a_cursor_page(SqlDialect dialect)
    {
        AssertSharedRegionsMatch(
            dialect,
            new PageCandidateMode.Cursor(),
            new PageCandidateMode.Cursor(OrderingMode: PageOrderingMode.ContentVersion),
            withAuthorization: true
        );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_produce_an_identical_authorization_fragment_for_the_candidate_relation(
        SqlDialect dialect
    )
    {
        AssertSharedRegionsMatch(
            dialect,
            new PageCandidateMode.UnpagedCandidates(),
            new PageCandidateMode.UnpagedCandidates(OrderingMode: PageOrderingMode.ContentVersion),
            withAuthorization: true
        );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_produce_an_identical_authorization_fragment_for_a_traditional_page(
        SqlDialect dialect
    )
    {
        AssertSharedRegionsMatch(
            dialect,
            new PageCandidateMode.Traditional(),
            new PageCandidateMode.Traditional(OrderingMode: PageOrderingMode.ContentVersion),
            withAuthorization: true
        );
    }

    /// <summary>
    /// The parameter inventory is anchor-independent too, so a plan's bindings cannot shift with the
    /// column its bounds compare against.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_produce_identical_filter_parameters_under_either_anchor(SqlDialect dialect)
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var authorization = CandidateModeTestSpecs.CreateNamespaceAuthorization(dialect);

        var documentIdAnchored = compiler.Compile(
            CandidateModeTestSpecs.CreateSpec(new PageCandidateMode.Cursor(), authorization: authorization)
        );
        var contentVersionAnchored = compiler.Compile(
            CandidateModeTestSpecs.CreateSpec(
                new PageCandidateMode.Cursor(OrderingMode: PageOrderingMode.ContentVersion),
                authorization: authorization
            )
        );

        CandidateSqlRegions
            .FilterParameters(contentVersionAnchored)
            .Should()
            .Equal(CandidateSqlRegions.FilterParameters(documentIdAnchored));
    }

    private static void AssertSharedRegionsMatch(
        SqlDialect dialect,
        PageCandidateMode documentIdAnchoredMode,
        PageCandidateMode contentVersionAnchoredMode,
        bool withAuthorization
    )
    {
        var compiler = new PageDocumentIdSqlCompiler(dialect);
        var authorization = withAuthorization
            ? CandidateModeTestSpecs.CreateNamespaceAuthorization(dialect)
            : null;

        var documentIdRegion = CandidateSqlRegions.SharedCandidateRegion(
            compiler
                .Compile(
                    CandidateModeTestSpecs.CreateSpec(documentIdAnchoredMode, authorization: authorization)
                )
                .PageDocumentIdSql,
            documentIdAnchoredMode,
            dialect
        );
        var contentVersionRegion = CandidateSqlRegions.SharedCandidateRegion(
            compiler
                .Compile(
                    CandidateModeTestSpecs.CreateSpec(
                        contentVersionAnchoredMode,
                        authorization: authorization
                    )
                )
                .PageDocumentIdSql,
            contentVersionAnchoredMode,
            dialect
        );

        if (withAuthorization)
        {
            documentIdRegion
                .Should()
                .Contain("Namespace", "the arrangement must emit an authorization fragment");
        }

        contentVersionRegion.Should().Be(documentIdRegion);
    }
}
