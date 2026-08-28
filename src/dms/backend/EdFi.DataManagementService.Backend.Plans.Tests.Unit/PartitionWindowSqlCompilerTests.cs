// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// The partition window statement. It wraps the shared unpaged candidate relation, so every filter,
/// change-version predicate, and authorization check is applied before any row is numbered or counted,
/// and it returns starting anchor values only.
/// </summary>
[TestFixture]
public class PartitionWindowSqlCompilerTests
{
    private static PageDocumentIdSqlPlan CompileWindow(
        SqlDialect dialect,
        PageCandidateMode.UnpagedCandidates? mode = null,
        string filterParameterName = "schoolYear",
        PageDocumentIdAuthorizationSpec? authorization = null
    )
    {
        PageCandidateMode.UnpagedCandidates candidateMode = mode ?? new PageCandidateMode.UnpagedCandidates();
        PageDocumentIdSqlPlan candidatePlan = new PageDocumentIdSqlCompiler(dialect).Compile(
            CandidateModeTestSpecs.CreateSpec(candidateMode, filterParameterName, authorization)
        );

        return new PartitionWindowSqlCompiler(dialect).Compile(candidatePlan, candidateMode);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Partition_Window_Statement : PartitionWindowSqlCompilerTests
    {
        private static readonly SqlDialect[] _dialects = [SqlDialect.Pgsql, SqlDialect.Mssql];

        [TestCaseSource(nameof(_dialects))]
        public void It_wraps_the_candidate_relation_in_a_common_table_expression(SqlDialect dialect)
        {
            CompileWindow(dialect).PageDocumentIdSql.Should().Contain("WITH candidates AS (");
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_embeds_the_candidate_body_without_its_statement_terminator(SqlDialect dialect)
        {
            string sql = CompileWindow(dialect).PageDocumentIdSql;

            // One statement, and its only terminator is the last character. An interior terminator would
            // be invalid inside WITH ... AS ( ... ) and would also mean more than one command.
            sql.Count(character => character == ';').Should().Be(1);
            sql.TrimEnd().Should().EndWith(";");
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_applies_candidate_predicates_before_row_numbering(SqlDialect dialect)
        {
            string sql = CompileWindow(dialect).PageDocumentIdSql;

            sql.IndexOf("@schoolYear", StringComparison.Ordinal)
                .Should()
                .BeLessThan(
                    sql.IndexOf("ROW_NUMBER", StringComparison.Ordinal),
                    "boundaries are calculated over the filtered candidate set, so the filter cannot "
                        + "be applied after the rows are numbered"
                );
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_applies_authorization_before_row_numbering(SqlDialect dialect)
        {
            string sql = CompileWindow(
                dialect,
                authorization: CandidateModeTestSpecs.CreateNamespaceAuthorization(dialect)
            ).PageDocumentIdSql;

            sql.IndexOf("Namespace", StringComparison.Ordinal)
                .Should()
                .BeLessThan(sql.IndexOf("ROW_NUMBER", StringComparison.Ordinal));
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_numbers_candidates_by_ascending_document_id(SqlDialect dialect)
        {
            string quotedDocumentId = dialect == SqlDialect.Pgsql ? "\"DocumentId\"" : "[DocumentId]";

            CompileWindow(dialect)
                .PageDocumentIdSql.Should()
                .Contain($"ROW_NUMBER() OVER (ORDER BY pc.{quotedDocumentId})");
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_selects_the_start_rows_with_the_modulo_rule(SqlDialect dialect)
        {
            string sql = CompileWindow(dialect).PageDocumentIdSql;
            string quoted =
                dialect == SqlDialect.Pgsql
                    ? "(ps.\"row_number\" - 1) % ps.\"partition_size\" = 0"
                    : "(ps.[row_number] - 1) % ps.[partition_size] = 0";

            sql.Should().Contain(quoted);
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_returns_the_boundary_document_ids_ordered_ascending(SqlDialect dialect)
        {
            string quotedDocumentId = dialect == SqlDialect.Pgsql ? "\"DocumentId\"" : "[DocumentId]";
            string sql = CompileWindow(dialect).PageDocumentIdSql;

            sql.Should().Contain($"SELECT ps.{quotedDocumentId}");
            sql.Should().Contain($"ORDER BY ps.{quotedDocumentId} ASC");
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_emits_no_hydration_or_paging_shapes(SqlDialect dialect)
        {
            string sql = CompileWindow(dialect).PageDocumentIdSql;

            sql.Should().NotContain("DISTINCT");
            sql.Should().NotContain("OFFSET");
            sql.Should().NotContain("FETCH");
            sql.Should().NotContain("TOP (");
            sql.Should().NotContain("LIMIT");
            sql.Should()
                .NotContain(
                    "INSERT",
                    "the endpoint materializes no keyset table: it returns identifiers and hydrates nothing"
                );
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_returns_no_total_count_plan(SqlDialect dialect)
        {
            PageDocumentIdSqlPlan plan = CompileWindow(dialect);

            plan.TotalCountSql.Should().BeNull();
            plan.TotalCountParametersInOrder.Should().BeNull();
            plan.PageDocumentIdSql.Should().NotContain("COUNT(1)");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Postgresql_Partition_Window : PartitionWindowSqlCompilerTests
    {
        private string _sql = null!;

        [SetUp]
        public void Setup()
        {
            _sql = CompileWindow(SqlDialect.Pgsql).PageDocumentIdSql;
        }

        [Test]
        public void It_counts_with_count_star()
        {
            _sql.Should().Contain("COUNT(*) OVER () AS \"candidate_count\"");
            _sql.Should().NotContain("COUNT_BIG");
        }

        [Test]
        public void It_divides_in_numeric_before_taking_the_ceiling()
        {
            _sql.Should()
                .Contain(
                    "CEIL(CAST(pr.\"candidate_count\" AS numeric) / CAST(@number AS numeric))",
                    "an integer quotient with a ceiling applied afterward is a no-op on an "
                        + "already-truncated value, which would return one partition more than requested"
                );
        }

        [Test]
        public void It_takes_the_greater_of_the_computed_and_minimum_sizes_as_bigint()
        {
            _sql.Should().Contain("GREATEST(CAST(CEIL(");
            _sql.Should().Contain("AS bigint), @minimumPartitionSize) AS \"partition_size\"");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Sql_Server_Partition_Window : PartitionWindowSqlCompilerTests
    {
        private string _sql = null!;

        [SetUp]
        public void Setup()
        {
            _sql = CompileWindow(SqlDialect.Mssql).PageDocumentIdSql;
        }

        [Test]
        public void It_counts_with_count_big()
        {
            _sql.Should()
                .Contain(
                    "COUNT_BIG(*) OVER () AS [candidate_count]",
                    "a candidate set larger than an int cannot be counted by COUNT"
                );
            _sql.Should().NotContain("COUNT(*)");
        }

        [Test]
        public void It_divides_in_decimal_before_taking_the_ceiling()
        {
            _sql.Should()
                .Contain(
                    "CEILING(CAST(pr.[candidate_count] AS decimal(28,0)) / CAST(@number AS decimal(10,0)))"
                );
        }

        [Test]
        public void It_takes_the_greater_of_the_computed_and_minimum_sizes_with_a_case_expression()
        {
            _sql.Should().Contain("CASE");
            _sql.Should().Contain("ELSE @minimumPartitionSize");
            _sql.Should()
                .NotContain(
                    "GREATEST",
                    "GREATEST requires SQL Server 2022 or later, and nothing in this repository "
                        + "establishes that floor for a deployment"
                );
        }

        [Test]
        public void It_converts_the_size_to_bigint_so_the_modulo_operands_match_row_number()
        {
            _sql.Should().Contain("AS bigint)");
            _sql.Should().Contain("[partition_size]");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Partition_Window_Parameter_Inventory : PartitionWindowSqlCompilerTests
    {
        private static readonly SqlDialect[] _dialects = [SqlDialect.Pgsql, SqlDialect.Mssql];

        [TestCaseSource(nameof(_dialects))]
        public void It_inventories_filter_parameters_then_the_two_partition_roles(SqlDialect dialect)
        {
            CompileWindow(dialect)
                .PageParametersInOrder.Select(parameter => (parameter.Role, parameter.ParameterName))
                .Should()
                .Equal(
                    (QuerySqlParameterRole.Filter, "schoolYear"),
                    (QuerySqlParameterRole.PartitionCount, "number"),
                    (QuerySqlParameterRole.MinimumPartitionSize, "minimumPartitionSize")
                );
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_binds_the_names_the_supplied_mode_owns(SqlDialect dialect)
        {
            PageDocumentIdSqlPlan plan = CompileWindow(
                dialect,
                new PageCandidateMode.UnpagedCandidates("partitionCount", "smallestPartition")
            );

            plan.PageParametersInOrder.Select(parameter => parameter.ParameterName)
                .Should()
                .Equal("schoolYear", "partitionCount", "smallestPartition");
            plan.PageDocumentIdSql.Should().Contain("@partitionCount");
            plan.PageDocumentIdSql.Should().Contain("@smallestPartition");
            plan.PageDocumentIdSql.Should().NotContain("@number");
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_rejects_a_filter_whose_name_collides_with_a_partition_parameter(SqlDialect dialect)
        {
            Action compile = () => CompileWindow(dialect, filterParameterName: "number");

            compile.Should().Throw<ArgumentException>();
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_rejects_a_candidate_plan_that_is_not_the_unpaged_relation(SqlDialect dialect)
        {
            PageDocumentIdSqlPlan traditionalPlan = new PageDocumentIdSqlCompiler(dialect).Compile(
                CandidateModeTestSpecs.CreateSpec(
                    new PageCandidateMode.Traditional(IncludeTotalCountSql: true)
                )
            );

            Action compile = () =>
                new PartitionWindowSqlCompiler(dialect).Compile(
                    traditionalPlan,
                    new PageCandidateMode.UnpagedCandidates()
                );

            compile.Should().Throw<ArgumentException>();
        }
    }

    /// <summary>
    /// The same statement over a <c>ContentVersion</c>-anchored candidate relation. That relation
    /// projects the anchor and nothing else, so every clause here has to follow it: a statement that
    /// ranked or projected <c>DocumentId</c> would name a column the relation it wraps does not have.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Content_Version_Anchored_Partition_Window : PartitionWindowSqlCompilerTests
    {
        private static readonly SqlDialect[] _dialects = [SqlDialect.Pgsql, SqlDialect.Mssql];

        private static string CompileAnchoredWindow(SqlDialect dialect)
        {
            return CompileWindow(
                dialect,
                new PageCandidateMode.UnpagedCandidates(OrderingMode: PageOrderingMode.ContentVersion)
            ).PageDocumentIdSql;
        }

        private static string QuotedContentVersion(SqlDialect dialect)
        {
            return dialect == SqlDialect.Pgsql ? "\"ContentVersion\"" : "[ContentVersion]";
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_numbers_candidates_by_ascending_content_version(SqlDialect dialect)
        {
            CompileAnchoredWindow(dialect)
                .Should()
                .Contain($"ROW_NUMBER() OVER (ORDER BY pc.{QuotedContentVersion(dialect)})");
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_carries_the_content_version_through_the_ranked_and_sized_expressions(
            SqlDialect dialect
        )
        {
            string sql = CompileAnchoredWindow(dialect);
            string quotedContentVersion = QuotedContentVersion(dialect);

            sql.Should().Contain($"pc.{quotedContentVersion},");
            sql.Should().Contain($"pr.{quotedContentVersion},");
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_returns_the_boundary_content_versions_ordered_ascending(SqlDialect dialect)
        {
            string sql = CompileAnchoredWindow(dialect);
            string quotedContentVersion = QuotedContentVersion(dialect);

            sql.Should().Contain($"SELECT ps.{quotedContentVersion}");
            sql.Should().Contain($"ORDER BY ps.{quotedContentVersion} ASC");
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_names_no_document_id_anywhere_in_the_statement(SqlDialect dialect)
        {
            // The wrapped relation has no DocumentId to name. This is the assertion that fails if any one
            // clause is left behind on the old anchor, which the per-clause assertions above would not
            // catch on their own.
            CompileAnchoredWindow(dialect).Should().NotContain("DocumentId");
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_leaves_the_sizing_and_counting_expressions_unchanged_by_the_anchor(SqlDialect dialect)
        {
            // Only the anchor column moves. Row numbering, counting, and the modulo start rule are
            // arithmetic over row numbers, so they are the same statement under either anchor.
            string anchored = CompileAnchoredWindow(dialect);
            string documentIdAnchored = CompileWindow(dialect).PageDocumentIdSql;
            string quotedContentVersion = QuotedContentVersion(dialect);
            string quotedDocumentId = dialect == SqlDialect.Pgsql ? "\"DocumentId\"" : "[DocumentId]";

            anchored
                .Replace(quotedContentVersion, quotedDocumentId, StringComparison.Ordinal)
                .Should()
                .Be(documentIdAnchored);
        }

        [TestCaseSource(nameof(_dialects))]
        public void It_inventories_the_same_parameters_under_either_anchor(SqlDialect dialect)
        {
            CompileWindow(
                dialect,
                new PageCandidateMode.UnpagedCandidates(OrderingMode: PageOrderingMode.ContentVersion)
            )
                .PageParametersInOrder.Select(parameter => (parameter.Role, parameter.ParameterName))
                .Should()
                .Equal(
                    (QuerySqlParameterRole.Filter, "schoolYear"),
                    (QuerySqlParameterRole.PartitionCount, "number"),
                    (QuerySqlParameterRole.MinimumPartitionSize, "minimumPartitionSize")
                );
        }
    }
}
