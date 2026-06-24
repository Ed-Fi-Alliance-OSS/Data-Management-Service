// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_NamespaceAuthorizationSqlCompiler
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbTableName _rootTable = new(_edfiSchema, "GradebookEntry");
    private static readonly DbColumnName _namespaceColumn = new("Namespace");

    private static NamespaceAuthorizationCheckSpec StoredCheck(int index = 0) =>
        new(index, NamespaceAuthorizationCheckValueSource.Stored, _rootTable, _namespaceColumn);

    private static NamespaceAuthorizationCheckSpec ProposedCheck(int index = 0) =>
        new(index, NamespaceAuthorizationCheckValueSource.Proposed, _rootTable, _namespaceColumn);

    private static string Sql(params string[] lines) => string.Join("\n", lines) + "\n";

    [Test]
    public void It_compiles_a_pgsql_stored_check_with_null_guarded_LIKE_ANY_and_AUTH1_throw_paths()
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            new NamespaceAuthorizationSqlSpec(
                [StoredCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                ),
                DocumentIdParameterName: "documentId",
                ProposedNamespaceParameterName: "proposedNamespace"
            )
        );

        plan.AuthorizationSql.Should()
            .Be(
                Sql(
                    "SELECT CASE",
                    "    WHEN EXISTS (SELECT 1 FROM \"edfi\".\"GradebookEntry\" r WHERE r.\"DocumentId\" = @documentId AND r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes)) THEN 1",
                    "    WHEN EXISTS (SELECT 1 FROM \"edfi\".\"GradebookEntry\" r WHERE r.\"DocumentId\" = @documentId AND (r.\"Namespace\" IS NULL OR r.\"Namespace\" = '')) THEN \"dms\".\"throw_error\"('AUTH1', 'ns1|0|u')",
                    "    WHEN NOT EXISTS (SELECT 1 FROM \"edfi\".\"GradebookEntry\" r WHERE r.\"DocumentId\" = @documentId) THEN \"dms\".\"throw_error\"('AUTH1', 'ns1|0|s')",
                    "    ELSE \"dms\".\"throw_error\"('AUTH1', 'ns1|0|m')",
                    "END;"
                )
            );
        plan.AuthorizationSql.Should()
            .Contain(
                "WHEN EXISTS (SELECT 1 FROM \"edfi\".\"GradebookEntry\" r WHERE r.\"DocumentId\" = @documentId AND r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes)) THEN 1"
            );
        plan.AuthorizationSql.Should()
            .Contain(
                "WHEN EXISTS (SELECT 1 FROM \"edfi\".\"GradebookEntry\" r WHERE r.\"DocumentId\" = @documentId AND (r.\"Namespace\" IS NULL OR r.\"Namespace\" = '')) THEN \"dms\".\"throw_error\"('AUTH1', 'ns1|0|u')"
            );
        // A target row missing entirely raises the stale stored-target kind ('s') rather than mismatch.
        plan.AuthorizationSql.Should()
            .Contain(
                "WHEN NOT EXISTS (SELECT 1 FROM \"edfi\".\"GradebookEntry\" r WHERE r.\"DocumentId\" = @documentId) THEN \"dms\".\"throw_error\"('AUTH1', 'ns1|0|s')"
            );
        plan.AuthorizationSql.Should().Contain("ELSE \"dms\".\"throw_error\"('AUTH1', 'ns1|0|m')");
        plan.ParametersInOrder.Select(static p => p.ParameterName)
            .Should()
            .Equal("documentId", "namespacePrefixes");
    }

    [Test]
    public void It_compiles_a_mssql_stored_check_with_OR_chain_LIKE_and_CAST_AUTH1_dash_throw_paths()
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Mssql);

        var plan = compiler.Compile(
            new NamespaceAuthorizationSqlSpec(
                [StoredCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Mssql,
                    ["uri://ed-fi.org/", "uri://gbisd.edu/"],
                    "namespacePrefixes"
                ),
                DocumentIdParameterName: "documentId",
                ProposedNamespaceParameterName: "proposedNamespace"
            )
        );

        plan.AuthorizationSql.Should()
            .Contain(
                "WHEN EXISTS (SELECT 1 FROM [edfi].[GradebookEntry] r WHERE r.[DocumentId] = @documentId AND r.[Namespace] IS NOT NULL AND (r.[Namespace] LIKE @namespacePrefixes_0 ESCAPE '\\' OR r.[Namespace] LIKE @namespacePrefixes_1 ESCAPE '\\')) THEN 1"
            );
        plan.AuthorizationSql.Should()
            .Contain(
                "WHEN EXISTS (SELECT 1 FROM [edfi].[GradebookEntry] r WHERE r.[DocumentId] = @documentId AND (r.[Namespace] IS NULL OR r.[Namespace] = '')) THEN CAST('AUTH1 - ns1|0|u' AS INT)"
            );
        plan.AuthorizationSql.Should()
            .Contain(
                "WHEN NOT EXISTS (SELECT 1 FROM [edfi].[GradebookEntry] r WHERE r.[DocumentId] = @documentId) THEN CAST('AUTH1 - ns1|0|s' AS INT)"
            );
        plan.AuthorizationSql.Should().Contain("ELSE CAST('AUTH1 - ns1|0|m' AS INT)");
        plan.ParametersInOrder.Select(static p => p.ParameterName)
            .Should()
            .Equal("documentId", "namespacePrefixes_0", "namespacePrefixes_1");
    }

    [Test]
    public void It_compiles_a_pgsql_proposed_check_with_null_guard_then_LIKE_ANY_match_then_mismatch_throw()
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            new NamespaceAuthorizationSqlSpec(
                [ProposedCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                ),
                DocumentIdParameterName: "documentId",
                ProposedNamespaceParameterName: "proposedNamespace"
            )
        );

        plan.AuthorizationSql.Should()
            .Be(
                Sql(
                    "SELECT CASE",
                    "    WHEN (@proposedNamespace IS NULL OR @proposedNamespace = '') THEN \"dms\".\"throw_error\"('AUTH1', 'ns1|0|r')",
                    "    WHEN @proposedNamespace LIKE ANY(@namespacePrefixes) THEN 1",
                    "    ELSE \"dms\".\"throw_error\"('AUTH1', 'ns1|0|m')",
                    "END;"
                )
            );
        plan.AuthorizationSql.Should()
            .Contain(
                "WHEN (@proposedNamespace IS NULL OR @proposedNamespace = '') THEN \"dms\".\"throw_error\"('AUTH1', 'ns1|0|r')"
            );
        plan.AuthorizationSql.Should().Contain("WHEN @proposedNamespace LIKE ANY(@namespacePrefixes) THEN 1");
        plan.AuthorizationSql.Should().Contain("ELSE \"dms\".\"throw_error\"('AUTH1', 'ns1|0|m')");
        plan.ParametersInOrder.Select(static p => p.ParameterName)
            .Should()
            .Equal("proposedNamespace", "namespacePrefixes");
    }

    [Test]
    public void It_compiles_a_mssql_proposed_check_with_null_guard_then_OR_chain_match_then_mismatch_throw()
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Mssql);

        var plan = compiler.Compile(
            new NamespaceAuthorizationSqlSpec(
                [ProposedCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Mssql,
                    ["uri://ed-fi.org/", "uri://gbisd.edu/"],
                    "namespacePrefixes"
                ),
                DocumentIdParameterName: "documentId",
                ProposedNamespaceParameterName: "proposedNamespace"
            )
        );

        plan.AuthorizationSql.Should()
            .Contain(
                "WHEN (@proposedNamespace IS NULL OR @proposedNamespace = '') THEN CAST('AUTH1 - ns1|0|r' AS INT)"
            );
        plan.AuthorizationSql.Should()
            .Contain(
                "WHEN (@proposedNamespace LIKE @namespacePrefixes_0 ESCAPE '\\' OR @proposedNamespace LIKE @namespacePrefixes_1 ESCAPE '\\') THEN 1"
            );
        plan.AuthorizationSql.Should().Contain("ELSE CAST('AUTH1 - ns1|0|m' AS INT)");
    }

    [Test]
    public void It_classifies_empty_namespace_values_identically_to_null_on_both_stored_and_proposed_checks()
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            new NamespaceAuthorizationSqlSpec(
                [StoredCheck(0), ProposedCheck(1)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                ),
                DocumentIdParameterName: "documentId",
                ProposedNamespaceParameterName: "proposedNamespace"
            )
        );

        // Stored uninitialized branch matches NULL or empty so legacy rows with empty namespace
        // are classified as uninitialized rather than mismatch.
        plan.AuthorizationSql.Should().Contain("(r.\"Namespace\" IS NULL OR r.\"Namespace\" = '')");
        // Proposed missing branch matches NULL or empty so an empty proposed value classifies as
        // missing rather than mismatch.
        plan.AuthorizationSql.Should().Contain("(@proposedNamespace IS NULL OR @proposedNamespace = '')");
    }

    [Test]
    public void It_co_batches_a_stored_then_proposed_pair_into_one_command_with_statements_separated_by_semicolon_and_distinct_indices()
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            new NamespaceAuthorizationSqlSpec(
                [StoredCheck(0), ProposedCheck(1)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                ),
                DocumentIdParameterName: "documentId",
                ProposedNamespaceParameterName: "proposedNamespace"
            )
        );

        plan.AuthorizationSql.Should().Contain("'ns1|0|u'");
        plan.AuthorizationSql.Should().Contain("'ns1|0|m'");
        plan.AuthorizationSql.Should().Contain("'ns1|1|r'");
        plan.AuthorizationSql.Should().Contain("'ns1|1|m'");

        var storedIndex = plan.AuthorizationSql.IndexOf("'ns1|0|", StringComparison.Ordinal);
        var proposedIndex = plan.AuthorizationSql.IndexOf("'ns1|1|", StringComparison.Ordinal);
        storedIndex.Should().BeGreaterThan(-1);
        proposedIndex.Should().BeGreaterThan(storedIndex);

        plan.AuthorizationSql.Split(';').Where(part => part.Trim().Length > 0).Should().HaveCount(2);

        plan.ParametersInOrder.Select(static p => p.ParameterName)
            .Should()
            .Equal("documentId", "proposedNamespace", "namespacePrefixes");
    }

    [Test]
    public void It_emits_AUTH1_payloads_strictly_in_the_form_ns1_pipe_index_pipe_kind()
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            new NamespaceAuthorizationSqlSpec(
                [StoredCheck(0), ProposedCheck(1)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                ),
                DocumentIdParameterName: "documentId",
                ProposedNamespaceParameterName: "proposedNamespace"
            )
        );

        // No relationship-discriminator payloads should ever leak into namespace SQL. The stored check
        // emits the 's' (stale stored-target) kind in addition to 'u' and 'm'.
        plan.AuthorizationSql.Should().NotContain("'1|");
        plan.AuthorizationSql.Should().NotMatchRegex(@"AUTH1[^']*\bns1\|0\|[^urms]");
        plan.AuthorizationSql.Should().NotMatchRegex(@"AUTH1[^']*\bns1\|1\|[^urms]");
    }

    [Test]
    public void It_throws_when_namespace_check_specs_target_different_root_tables()
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var spec = new NamespaceAuthorizationSqlSpec(
            [
                StoredCheck(0),
                new NamespaceAuthorizationCheckSpec(
                    1,
                    NamespaceAuthorizationCheckValueSource.Proposed,
                    new DbTableName(_edfiSchema, "OtherRootTable"),
                    _namespaceColumn
                ),
            ],
            NamespacePrefixParameterizationFactory.Create(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/"],
                "namespacePrefixes"
            ),
            DocumentIdParameterName: "documentId",
            ProposedNamespaceParameterName: "proposedNamespace"
        );

        var act = () => compiler.Compile(spec);

        act.Should().Throw<ArgumentException>().WithMessage("*share one root table*");
    }

    [Test]
    public void It_throws_when_namespace_checks_are_empty()
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var spec = new NamespaceAuthorizationSqlSpec(
            [],
            NamespacePrefixParameterizationFactory.Create(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/"],
                "namespacePrefixes"
            ),
            DocumentIdParameterName: "documentId",
            ProposedNamespaceParameterName: "proposedNamespace"
        );

        var act = () => compiler.Compile(spec);

        act.Should().Throw<ArgumentException>().WithMessage("*at least one check*");
    }

    [TestCase(
        "document-id",
        "proposedNamespace",
        nameof(NamespaceAuthorizationSqlSpec.DocumentIdParameterName)
    )]
    [TestCase(
        "documentId",
        "proposed-namespace",
        nameof(NamespaceAuthorizationSqlSpec.ProposedNamespaceParameterName)
    )]
    public void It_rejects_invalid_single_record_namespace_authorization_parameter_names(
        string documentIdParameterName,
        string proposedNamespaceParameterName,
        string expectedParameterName
    )
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var spec = new NamespaceAuthorizationSqlSpec(
            [StoredCheck(0)],
            NamespacePrefixParameterizationFactory.Create(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/"],
                "namespacePrefixes"
            ),
            documentIdParameterName,
            proposedNamespaceParameterName
        );

        var act = () => compiler.Compile(spec);

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("Parameter name must match pattern*")
            .WithParameterName(expectedParameterName);
    }

    [Test]
    public void It_throws_when_a_namespace_check_uses_an_unsupported_value_source()
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var spec = new NamespaceAuthorizationSqlSpec(
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    (NamespaceAuthorizationCheckValueSource)99,
                    _rootTable,
                    _namespaceColumn
                ),
            ],
            NamespacePrefixParameterizationFactory.Create(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/"],
                "namespacePrefixes"
            ),
            DocumentIdParameterName: "documentId",
            ProposedNamespaceParameterName: "proposedNamespace"
        );

        var act = () => compiler.Compile(spec);

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Unsupported namespace authorization value source*");
    }

    [Test]
    public void It_does_not_include_the_documentId_parameter_when_only_proposed_checks_are_planned()
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            new NamespaceAuthorizationSqlSpec(
                [ProposedCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                ),
                DocumentIdParameterName: "documentId",
                ProposedNamespaceParameterName: "proposedNamespace"
            )
        );

        plan.ParametersInOrder.Select(static p => p.ParameterName)
            .Should()
            .Equal("proposedNamespace", "namespacePrefixes");
    }

    [Test]
    public void It_does_not_include_the_proposed_namespace_parameter_when_only_stored_checks_are_planned()
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            new NamespaceAuthorizationSqlSpec(
                [StoredCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                ),
                DocumentIdParameterName: "documentId",
                ProposedNamespaceParameterName: "proposedNamespace"
            )
        );

        plan.ParametersInOrder.Select(static p => p.ParameterName)
            .Should()
            .Equal("documentId", "namespacePrefixes");
    }

    [Test]
    public void It_throws_when_a_pgsql_array_parameterization_is_handed_to_a_mssql_compiler()
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Mssql);

        var spec = new NamespaceAuthorizationSqlSpec(
            [StoredCheck(0)],
            NamespacePrefixParameterizationFactory.Create(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/"],
                "namespacePrefixes"
            ),
            DocumentIdParameterName: "documentId",
            ProposedNamespaceParameterName: "proposedNamespace"
        );

        var act = () => compiler.Compile(spec);

        act.Should().Throw<ArgumentException>().WithMessage("*not supported by SQL dialect 'Mssql'*");
    }

    [Test]
    public void It_throws_when_a_mssql_scalar_parameterization_is_handed_to_a_pgsql_compiler()
    {
        var compiler = new NamespaceAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var spec = new NamespaceAuthorizationSqlSpec(
            [StoredCheck(0)],
            NamespacePrefixParameterizationFactory.Create(
                SqlDialect.Mssql,
                ["uri://ed-fi.org/"],
                "namespacePrefixes"
            ),
            DocumentIdParameterName: "documentId",
            ProposedNamespaceParameterName: "proposedNamespace"
        );

        var act = () => compiler.Compile(spec);

        act.Should().Throw<ArgumentException>().WithMessage("*not supported by SQL dialect 'Pgsql'*");
    }
}
