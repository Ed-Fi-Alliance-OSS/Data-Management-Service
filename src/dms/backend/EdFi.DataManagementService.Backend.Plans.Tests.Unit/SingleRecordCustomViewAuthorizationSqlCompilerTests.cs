// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_SingleRecordCustomViewAuthorizationSqlCompiler
{
    private const string DocumentIdParameter = "documentId";

    private static readonly DbSchemaName EdFiSchema = new("edfi");
    private static readonly DbSchemaName AuthSchema = new("auth");
    private static readonly DbSchemaName DmsSchema = new("dms");
    private static readonly DbColumnName DocumentIdColumn = new("DocumentId");

    private static DbTableName Table(string name) => new(EdFiSchema, name);

    private static DbColumnName Col(string name) => new(name);

    private static DbTableName AuthView(string strategyName) => new(AuthSchema, strategyName);

    private static SingleRecordCustomViewAuthorizationCheckSpec Check(
        int index,
        CustomViewAuthorizationCheckValueSource valueSource,
        IReadOnlyList<ColumnPathStep> path,
        CustomViewAuthorizationCheckTarget checkTarget,
        string strategyName = "StudentWithCTECourseEnrollments"
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(strategyName, 0),
            index,
            valueSource,
            AuthView(strategyName),
            DocumentIdColumn,
            path,
            checkTarget,
            new QualifiedResourceName("Ed-Fi", "Student"),
            ["StudentUniqueId"],
            "You may need a Student with CTE Course Enrollments."
        );

    /// <summary>CourseTranscript.Student_DocumentId -> Student. One root-owned hop.</summary>
    private static IReadOnlyList<ColumnPathStep> DirectPath() =>
        [new ColumnPathStep(Table("CourseTranscript"), Col("Student_DocumentId"), null, null)];

    /// <summary>Student.DocumentId. The basis is the subject.</summary>
    private static IReadOnlyList<ColumnPathStep> SelfBasisPath() =>
        [new ColumnPathStep(Table("Student"), DocumentIdColumn, null, null)];

    /// <summary>CourseTranscript -> StudentAcademicRecord -> Student.</summary>
    private static IReadOnlyList<ColumnPathStep> TransitivePath() =>
        [
            new ColumnPathStep(
                Table("CourseTranscript"),
                Col("StudentAcademicRecord_DocumentId"),
                Table("StudentAcademicRecord"),
                DocumentIdColumn
            ),
            new ColumnPathStep(Table("StudentAcademicRecord"), Col("Student_DocumentId"), null, null),
        ];

    private static CustomViewAuthorizationCheckTarget.Stored StoredTarget(string rootTable) =>
        new(Table(rootTable), DocumentIdColumn);

    private static CustomViewAuthorizationCheckTarget.Proposed ProposedTarget(
        string rootTable,
        string column,
        int index
    ) =>
        new(
            Table(rootTable),
            new CustomViewAuthorizationProposedValueBinding(
                Table(rootTable),
                Col(column),
                $"logical:{column}",
                $"customViewAuthorization{index}"
            )
        );

    private static SingleRecordCustomViewAuthorizationSqlPlan Compile(
        SqlDialect dialect,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checks,
        string? rowGuardPredicateSql = null
    ) =>
        new SingleRecordCustomViewAuthorizationSqlCompiler(dialect).Compile(
            new SingleRecordCustomViewAuthorizationSqlSpec(checks, DocumentIdParameter, rowGuardPredicateSql)
        );

    /// <summary>
    /// Splits the batch into statements. Trimming first is required: the compiler ends each statement with a
    /// newline, so a trailing whitespace-only entry would otherwise survive RemoveEmptyEntries.
    /// </summary>
    private static string[] SplitStatements(string sql) =>
        sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Normalize(string sql) =>
        string.Join(' ', sql.Split((char[])['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    [Test]
    public void It_should_compile_a_stored_direct_path_check_for_postgresql()
    {
        var plan = Compile(
            SqlDialect.Pgsql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    DirectPath(),
                    StoredTarget("CourseTranscript")
                ),
            ]
        );

        Normalize(plan.AuthorizationSql)
            .Should()
            .Be(
                "SELECT CASE "
                    + "WHEN EXISTS (SELECT 1 FROM \"edfi\".\"CourseTranscript\" r "
                    + "WHERE r.\"DocumentId\" = @documentId "
                    + "AND r.\"Student_DocumentId\" IN (SELECT cv.\"DocumentId\" FROM \"auth\".\"StudentWithCTECourseEnrollments\" cv)) THEN 1 "
                    + "WHEN EXISTS (SELECT 1 FROM \"edfi\".\"CourseTranscript\" r "
                    + "WHERE r.\"DocumentId\" = @documentId "
                    + "AND (r.\"Student_DocumentId\" IS NULL)) THEN \"dms\".\"throw_error\"('AUTH1', 'cv1|0|u') "
                    + "WHEN NOT EXISTS (SELECT 1 FROM \"edfi\".\"CourseTranscript\" r "
                    + "WHERE r.\"DocumentId\" = @documentId) THEN \"dms\".\"throw_error\"('AUTH1', 'cv1|0|s') "
                    + "ELSE \"dms\".\"throw_error\"('AUTH1', 'cv1|0|n') "
                    + "END;"
            );
    }

    [Test]
    public void It_should_compile_a_stored_direct_path_check_for_sql_server()
    {
        var plan = Compile(
            SqlDialect.Mssql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    DirectPath(),
                    StoredTarget("CourseTranscript")
                ),
            ]
        );

        Normalize(plan.AuthorizationSql)
            .Should()
            .Be(
                "SELECT CASE "
                    + "WHEN EXISTS (SELECT 1 FROM [edfi].[CourseTranscript] r "
                    + "WHERE r.[DocumentId] = @documentId "
                    + "AND r.[Student_DocumentId] IN (SELECT cv.[DocumentId] FROM [auth].[StudentWithCTECourseEnrollments] cv)) THEN 1 "
                    + "WHEN EXISTS (SELECT 1 FROM [edfi].[CourseTranscript] r "
                    + "WHERE r.[DocumentId] = @documentId "
                    + "AND (r.[Student_DocumentId] IS NULL)) THEN CAST('AUTH1 - cv1|0|u' AS INT) "
                    + "WHEN NOT EXISTS (SELECT 1 FROM [edfi].[CourseTranscript] r "
                    + "WHERE r.[DocumentId] = @documentId) THEN CAST('AUTH1 - cv1|0|s' AS INT) "
                    + "ELSE CAST('AUTH1 - cv1|0|n' AS INT) "
                    + "END;"
            );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_quote_the_custom_view_identifier_for_the_dialect(SqlDialect dialect)
    {
        // auth.md requires case-sensitive matching through quoted identifiers on both engines, which is what
        // makes quoted PascalCase DDL mandatory on PostgreSQL.
        var plan = Compile(
            dialect,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    DirectPath(),
                    StoredTarget("CourseTranscript")
                ),
            ]
        );

        var expectedAuthView =
            dialect is SqlDialect.Pgsql
                ? "\"auth\".\"StudentWithCTECourseEnrollments\""
                : "[auth].[StudentWithCTECourseEnrollments]";

        plan.AuthorizationSql.Should().Contain(expectedAuthView);
        plan.AuthorizationSql.Should().NotContain("auth.StudentWithCTECourseEnrollments");
    }

    [Test]
    public void It_should_omit_the_uninitialized_branch_for_a_self_basis_stored_check()
    {
        // The path terminates on the root's own DocumentId, which is NOT NULL, so an uninitialized branch
        // would be dead SQL.
        var plan = Compile(
            SqlDialect.Pgsql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    SelfBasisPath(),
                    StoredTarget("Student")
                ),
            ]
        );

        Normalize(plan.AuthorizationSql)
            .Should()
            .Be(
                "SELECT CASE "
                    + "WHEN EXISTS (SELECT 1 FROM \"edfi\".\"Student\" r "
                    + "WHERE r.\"DocumentId\" = @documentId "
                    + "AND r.\"DocumentId\" IN (SELECT cv.\"DocumentId\" FROM \"auth\".\"StudentWithCTECourseEnrollments\" cv)) THEN 1 "
                    + "WHEN NOT EXISTS (SELECT 1 FROM \"edfi\".\"Student\" r "
                    + "WHERE r.\"DocumentId\" = @documentId) THEN \"dms\".\"throw_error\"('AUTH1', 'cv1|0|s') "
                    + "ELSE \"dms\".\"throw_error\"('AUTH1', 'cv1|0|n') "
                    + "END;"
            );
        plan.AuthorizationSql.Should().NotContain("cv1|0|u");
    }

    [Test]
    public void It_should_correlate_a_stored_transitive_path_to_the_addressed_row()
    {
        // No uncorrelated root re-scan: the target is already bound, unlike the GET-many page shape.
        var plan = Compile(
            SqlDialect.Pgsql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    TransitivePath(),
                    StoredTarget("CourseTranscript")
                ),
            ]
        );

        Normalize(plan.AuthorizationSql)
            .Should()
            .Be(
                "SELECT CASE "
                    + "WHEN EXISTS (SELECT 1 FROM \"edfi\".\"CourseTranscript\" r "
                    + "JOIN \"edfi\".\"StudentAcademicRecord\" t1 ON t1.\"DocumentId\" = r.\"StudentAcademicRecord_DocumentId\" "
                    + "WHERE r.\"DocumentId\" = @documentId "
                    + "AND t1.\"Student_DocumentId\" IN (SELECT cv.\"DocumentId\" FROM \"auth\".\"StudentWithCTECourseEnrollments\" cv)) THEN 1 "
                    + "WHEN EXISTS (SELECT 1 FROM \"edfi\".\"CourseTranscript\" r "
                    + "LEFT JOIN \"edfi\".\"StudentAcademicRecord\" t1 ON t1.\"DocumentId\" = r.\"StudentAcademicRecord_DocumentId\" "
                    + "WHERE r.\"DocumentId\" = @documentId "
                    + "AND (r.\"StudentAcademicRecord_DocumentId\" IS NULL OR t1.\"Student_DocumentId\" IS NULL)) "
                    + "THEN \"dms\".\"throw_error\"('AUTH1', 'cv1|0|u') "
                    + "WHEN NOT EXISTS (SELECT 1 FROM \"edfi\".\"CourseTranscript\" r "
                    + "WHERE r.\"DocumentId\" = @documentId) THEN \"dms\".\"throw_error\"('AUTH1', 'cv1|0|s') "
                    + "ELSE \"dms\".\"throw_error\"('AUTH1', 'cv1|0|n') "
                    + "END;"
            );
    }

    [Test]
    public void It_should_compile_a_proposed_direct_path_check_for_postgresql()
    {
        var plan = Compile(
            SqlDialect.Pgsql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Proposed,
                    DirectPath(),
                    ProposedTarget("CourseTranscript", "Student_DocumentId", 0)
                ),
            ]
        );

        Normalize(plan.AuthorizationSql)
            .Should()
            .Be(
                "SELECT CASE "
                    + "WHEN @customViewAuthorization0 IS NULL THEN \"dms\".\"throw_error\"('AUTH1', 'cv1|0|r') "
                    + "WHEN @customViewAuthorization0 IN (SELECT cv.\"DocumentId\" FROM \"auth\".\"StudentWithCTECourseEnrollments\" cv) THEN 1 "
                    + "ELSE \"dms\".\"throw_error\"('AUTH1', 'cv1|0|n') "
                    + "END;"
            );
    }

    [Test]
    public void It_should_compile_a_proposed_direct_path_check_for_sql_server()
    {
        var plan = Compile(
            SqlDialect.Mssql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Proposed,
                    DirectPath(),
                    ProposedTarget("CourseTranscript", "Student_DocumentId", 0)
                ),
            ]
        );

        Normalize(plan.AuthorizationSql)
            .Should()
            .Be(
                "SELECT CASE "
                    + "WHEN @customViewAuthorization0 IS NULL THEN CAST('AUTH1 - cv1|0|r' AS INT) "
                    + "WHEN @customViewAuthorization0 IN (SELECT cv.[DocumentId] FROM [auth].[StudentWithCTECourseEnrollments] cv) THEN 1 "
                    + "ELSE CAST('AUTH1 - cv1|0|n' AS INT) "
                    + "END;"
            );
    }

    [Test]
    public void It_should_start_a_proposed_transitive_path_at_the_row_the_bound_value_addresses()
    {
        var plan = Compile(
            SqlDialect.Pgsql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Proposed,
                    TransitivePath(),
                    ProposedTarget("CourseTranscript", "StudentAcademicRecord_DocumentId", 0)
                ),
            ]
        );

        Normalize(plan.AuthorizationSql)
            .Should()
            .Be(
                "SELECT CASE "
                    + "WHEN @customViewAuthorization0 IS NULL THEN \"dms\".\"throw_error\"('AUTH1', 'cv1|0|r') "
                    + "WHEN EXISTS (SELECT 1 FROM \"edfi\".\"StudentAcademicRecord\" t1 "
                    + "WHERE t1.\"DocumentId\" = @customViewAuthorization0 "
                    + "AND t1.\"Student_DocumentId\" IN (SELECT cv.\"DocumentId\" FROM \"auth\".\"StudentWithCTECourseEnrollments\" cv)) THEN 1 "
                    + "WHEN EXISTS (SELECT 1 FROM \"edfi\".\"StudentAcademicRecord\" t1 "
                    + "WHERE t1.\"DocumentId\" = @customViewAuthorization0 "
                    + "AND (t1.\"Student_DocumentId\" IS NULL)) THEN \"dms\".\"throw_error\"('AUTH1', 'cv1|0|r') "
                    + "ELSE \"dms\".\"throw_error\"('AUTH1', 'cv1|0|n') "
                    + "END;"
            );
    }

    [Test]
    public void It_should_emit_a_descriptor_basis_check_against_the_shared_descriptor_document_id()
    {
        var descriptorPath = new[]
        {
            new ColumnPathStep(
                Table("StudentTransportation"),
                Col("TransportationTypeDescriptor_DescriptorId"),
                new DbTableName(DmsSchema, "Descriptor"),
                DocumentIdColumn
            ),
        };

        var plan = Compile(
            SqlDialect.Pgsql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    descriptorPath,
                    StoredTarget("StudentTransportation"),
                    "TransportationTypeDescriptorWithABus"
                ),
            ]
        );

        // The descriptor FK already holds the descriptor's DocumentId, so the terminal step's dms.Descriptor
        // target drives no extra join.
        Normalize(plan.AuthorizationSql)
            .Should()
            .Contain(
                "AND r.\"TransportationTypeDescriptor_DescriptorId\" IN "
                    + "(SELECT cv.\"DocumentId\" FROM \"auth\".\"TransportationTypeDescriptorWithABus\" cv)"
            );
        plan.AuthorizationSql.Should().NotContain("Descriptor\" t1");
    }

    [Test]
    public void It_should_emit_stored_checks_before_proposed_checks_with_matching_payload_indexes()
    {
        var plan = Compile(
            SqlDialect.Pgsql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    DirectPath(),
                    StoredTarget("CourseTranscript")
                ),
                Check(
                    1,
                    CustomViewAuthorizationCheckValueSource.Proposed,
                    DirectPath(),
                    ProposedTarget("CourseTranscript", "Student_DocumentId", 1)
                ),
            ]
        );

        plan.EmittedCheckIndexesInOrder.Should().Equal(0, 1);
        var statements = SplitStatements(plan.AuthorizationSql);
        statements.Should().HaveCount(2);
        statements[0].Should().Contain("cv1|0|s").And.NotContain("cv1|1");
        statements[1].Should().Contain("cv1|1|r").And.NotContain("cv1|0");
    }

    [Test]
    public void It_should_emit_no_statement_for_a_self_basis_proposed_check()
    {
        // Its answer depends on whether a target was captured and on the paired stored check's outcome, so
        // it is decided in C#. The emitted-index list is what keeps result sets aligned to checks.
        var plan = Compile(
            SqlDialect.Pgsql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    SelfBasisPath(),
                    StoredTarget("Student")
                ),
                Check(
                    1,
                    CustomViewAuthorizationCheckValueSource.Proposed,
                    SelfBasisPath(),
                    new CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable(Table("Student"))
                ),
            ]
        );

        plan.EmittedCheckIndexesInOrder.Should().Equal(0);
        plan.ProposedValueParametersInOrder.Should().BeEmpty();
        SplitStatements(plan.AuthorizationSql).Should().HaveCount(1);
        plan.AuthorizationSql.Should().NotContain("cv1|1");
    }

    [Test]
    public void It_should_compile_to_empty_sql_when_every_check_is_decided_outside_sql()
    {
        var plan = Compile(
            SqlDialect.Pgsql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Proposed,
                    SelfBasisPath(),
                    new CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable(Table("Student"))
                ),
            ]
        );

        plan.AuthorizationSql.Should().BeEmpty();
        plan.EmittedCheckIndexesInOrder.Should().BeEmpty();
        plan.ParametersInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_should_append_the_row_guard_to_every_emitted_statement()
    {
        var plan = Compile(
            SqlDialect.Pgsql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    DirectPath(),
                    StoredTarget("CourseTranscript")
                ),
                Check(
                    1,
                    CustomViewAuthorizationCheckValueSource.Proposed,
                    DirectPath(),
                    ProposedTarget("CourseTranscript", "Student_DocumentId", 1)
                ),
            ],
            rowGuardPredicateSql: "target.DocumentId IS NOT NULL"
        );

        var statements = SplitStatements(plan.AuthorizationSql);
        statements.Should().HaveCount(2);
        statements
            .Should()
            .AllSatisfy(statement =>
                Normalize(statement).Should().EndWith("END WHERE target.DocumentId IS NOT NULL")
            );
    }

    [Test]
    public void It_should_bind_the_document_id_parameter_only_when_a_stored_check_is_present()
    {
        var storedOnly = Compile(
            SqlDialect.Pgsql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    DirectPath(),
                    StoredTarget("CourseTranscript")
                ),
            ]
        );
        var proposedOnly = Compile(
            SqlDialect.Pgsql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Proposed,
                    DirectPath(),
                    ProposedTarget("CourseTranscript", "Student_DocumentId", 0)
                ),
            ]
        );

        storedOnly
            .ParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("documentId");
        proposedOnly
            .ParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("customViewAuthorization0");
    }

    [Test]
    public void It_should_order_parameters_document_id_first_then_each_proposed_value()
    {
        var plan = Compile(
            SqlDialect.Pgsql,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    DirectPath(),
                    StoredTarget("CourseTranscript")
                ),
                Check(
                    1,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    TransitivePath(),
                    StoredTarget("CourseTranscript"),
                    "StudentWithAnIep"
                ),
                Check(
                    2,
                    CustomViewAuthorizationCheckValueSource.Proposed,
                    DirectPath(),
                    ProposedTarget("CourseTranscript", "Student_DocumentId", 2)
                ),
                Check(
                    3,
                    CustomViewAuthorizationCheckValueSource.Proposed,
                    TransitivePath(),
                    ProposedTarget("CourseTranscript", "StudentAcademicRecord_DocumentId", 3),
                    "StudentWithAnIep"
                ),
            ]
        );

        plan.ParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("documentId", "customViewAuthorization2", "customViewAuthorization3");
        plan.ProposedValueParametersInOrder.Select(parameter => parameter.CheckIndex).Should().Equal(2, 3);
        plan.EmittedCheckIndexesInOrder.Should().Equal(0, 1, 2, 3);
    }

    [Test]
    public void It_should_reject_an_empty_check_list()
    {
        var act = () => Compile(SqlDialect.Pgsql, []);

        act.Should().Throw<ArgumentException>().WithMessage("*at least one check spec*");
    }

    [Test]
    public void It_should_reject_a_gap_in_the_check_indexes()
    {
        // The cv1 payload reports only an index and the failure mapper resolves it positionally against the
        // request's planned list, so a gap would report a denial as some other check's category.
        var act = () =>
            Compile(
                SqlDialect.Pgsql,
                [
                    Check(
                        0,
                        CustomViewAuthorizationCheckValueSource.Stored,
                        DirectPath(),
                        StoredTarget("CourseTranscript")
                    ),
                    Check(
                        2,
                        CustomViewAuthorizationCheckValueSource.Stored,
                        DirectPath(),
                        StoredTarget("CourseTranscript")
                    ),
                ]
            );

        act.Should().Throw<ArgumentException>().WithMessage("*must run contiguously from 0*");
    }

    [Test]
    public void It_should_accept_a_batch_whose_indexes_start_above_zero()
    {
        // One request can emit several batches — views configured before and after a namespace check, or a
        // stored batch and a proposed one. Batches sharing a command share one provider exception, so their
        // indexes stay unique across the request instead of restarting at zero per batch.
        var plan = Compile(
            SqlDialect.Pgsql,
            [
                Check(
                    2,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    DirectPath(),
                    StoredTarget("CourseTranscript")
                ),
                Check(
                    3,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    TransitivePath(),
                    StoredTarget("CourseTranscript"),
                    "StudentWithAnIep"
                ),
            ]
        );

        plan.EmittedCheckIndexesInOrder.Should().Equal(2, 3);
        plan.AuthorizationSql.Should().Contain("cv1|2|n").And.Contain("cv1|3|n");
        plan.AuthorizationSql.Should().NotContain("cv1|0").And.NotContain("cv1|1");
    }

    [Test]
    public void It_should_reject_a_negative_first_check_index()
    {
        var act = () =>
            Compile(
                SqlDialect.Pgsql,
                [
                    Check(
                        -1,
                        CustomViewAuthorizationCheckValueSource.Stored,
                        DirectPath(),
                        StoredTarget("CourseTranscript")
                    ),
                ]
            );

        act.Should().Throw<ArgumentException>().WithMessage("*cannot be negative*");
    }

    [Test]
    public void It_should_reject_a_check_with_no_resolved_path()
    {
        var act = () =>
            Compile(
                SqlDialect.Pgsql,
                [
                    Check(
                        0,
                        CustomViewAuthorizationCheckValueSource.Stored,
                        [],
                        StoredTarget("CourseTranscript")
                    ),
                ]
            );

        act.Should().Throw<ArgumentException>().WithMessage("*no path to its basis resource*");
    }

    [Test]
    public void It_should_reject_checks_that_do_not_share_one_root_table()
    {
        var act = () =>
            Compile(
                SqlDialect.Pgsql,
                [
                    Check(
                        0,
                        CustomViewAuthorizationCheckValueSource.Stored,
                        DirectPath(),
                        StoredTarget("CourseTranscript")
                    ),
                    Check(
                        1,
                        CustomViewAuthorizationCheckValueSource.Stored,
                        SelfBasisPath(),
                        StoredTarget("Student")
                    ),
                ]
            );

        act.Should().Throw<ArgumentException>().WithMessage("*must share one root table*");
    }

    [Test]
    public void It_should_reject_an_unusable_proposed_parameter_seed()
    {
        var act = () =>
            Compile(
                SqlDialect.Pgsql,
                [
                    Check(
                        0,
                        CustomViewAuthorizationCheckValueSource.Proposed,
                        DirectPath(),
                        new CustomViewAuthorizationCheckTarget.Proposed(
                            Table("CourseTranscript"),
                            new CustomViewAuthorizationProposedValueBinding(
                                Table("CourseTranscript"),
                                Col("Student_DocumentId"),
                                "logical",
                                "not a valid parameter"
                            )
                        )
                    ),
                ]
            );

        act.Should().Throw<ArgumentException>();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_keep_the_branch_order_that_selects_the_problem_details_category(SqlDialect dialect)
    {
        var plan = Compile(
            dialect,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    DirectPath(),
                    StoredTarget("CourseTranscript")
                ),
            ]
        );

        var uninitializedPosition = plan.AuthorizationSql.IndexOf("cv1|0|u", StringComparison.Ordinal);
        var stalePosition = plan.AuthorizationSql.IndexOf("cv1|0|s", StringComparison.Ordinal);
        var mismatchPosition = plan.AuthorizationSql.IndexOf("cv1|0|n", StringComparison.Ordinal);
        var authorizedPosition = plan.AuthorizationSql.IndexOf("THEN 1", StringComparison.Ordinal);

        authorizedPosition.Should().BeLessThan(uninitializedPosition);
        uninitializedPosition.Should().BeLessThan(stalePosition);
        stalePosition.Should().BeLessThan(mismatchPosition);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_bind_no_list_valued_parameters(SqlDialect dialect)
    {
        // Custom-view checks bind at most the target DocumentId plus one value per proposed check, so they
        // never consume the table-valued-parameter budget that forces ordered segments elsewhere.
        var plan = Compile(
            dialect,
            [
                Check(
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    TransitivePath(),
                    StoredTarget("CourseTranscript")
                ),
                Check(
                    1,
                    CustomViewAuthorizationCheckValueSource.Proposed,
                    TransitivePath(),
                    ProposedTarget("CourseTranscript", "StudentAcademicRecord_DocumentId", 1)
                ),
            ]
        );

        plan.ParametersInOrder.Should().HaveCount(2);
    }
}
