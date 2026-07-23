// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_PageDocumentIdSqlCompiler_with_namespace_authorization
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbSchemaName _dmsSchema = new("dms");
    private static readonly DbTableName _rootTable = new(_edfiSchema, "GradebookEntry");
    private static readonly DbTableName _documentTable = new(_dmsSchema, "Document");
    private static readonly DbTableName _descriptorTable = new(_dmsSchema, "Descriptor");
    private static readonly DbColumnName _namespaceColumn = new("Namespace");

    [Test]
    public void It_emits_a_pgsql_LIKE_ANY_predicate_against_the_root_namespace_column_with_a_null_guard()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateNamespaceOnlySpec(SqlDialect.Pgsql, ["uri://ed-fi.org/", "uri://gbisd.edu/"])
        );

        plan.PageDocumentIdSql.Should()
            .Contain("(r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes))");
        plan.PageDocumentIdSql.Should().NotContain("OR r.\"Namespace\" LIKE");
        plan.PageParametersInOrder.Select(static p => p.ParameterName)
            .Should()
            .Equal("namespacePrefixes", "offset", "limit");
        plan.PageParametersInOrder.First(static p => p.ParameterName == "namespacePrefixes")
            .Binding.Kind.Should()
            .Be(QuerySqlParameterBindingKind.PgsqlArray);
    }

    [Test]
    public void It_emits_a_mssql_OR_chain_LIKE_predicate_against_the_root_namespace_column_with_a_null_guard()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);

        var plan = compiler.Compile(
            CreateNamespaceOnlySpec(
                SqlDialect.Mssql,
                ["uri://ed-fi.org/", "uri://gbisd.edu/", "uri://acme.test/"]
            )
        );

        plan.PageDocumentIdSql.Should()
            .Contain(
                "(r.[Namespace] IS NOT NULL AND ("
                    + "r.[Namespace] LIKE @namespacePrefixes_0 ESCAPE '\\' "
                    + "OR r.[Namespace] LIKE @namespacePrefixes_1 ESCAPE '\\' "
                    + "OR r.[Namespace] LIKE @namespacePrefixes_2 ESCAPE '\\'"
                    + "))"
            );
        plan.PageParametersInOrder.Select(static p => p.ParameterName)
            .Should()
            .Equal("namespacePrefixes_0", "namespacePrefixes_1", "namespacePrefixes_2", "offset", "limit");
    }

    [Test]
    public void It_emits_a_pgsql_LIKE_ANY_predicate_when_only_one_prefix_is_configured()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(CreateNamespaceOnlySpec(SqlDialect.Pgsql, ["uri://ed-fi.org/"]));

        plan.PageDocumentIdSql.Should()
            .Contain("(r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes))");
    }

    [Test]
    public void It_emits_namespace_AND_group_outside_relationship_OR_group_in_mixed_strategy_configs_pgsql()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateMixedSpec(
                SqlDialect.Pgsql,
                namespacePrefixes: ["uri://ed-fi.org/"],
                claimEducationOrganizationIds: [255901L, 100L]
            )
        );

        var namespaceIndex = plan.PageDocumentIdSql.IndexOf(
            "(r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes))",
            StringComparison.Ordinal
        );
        var relationshipGroupIndex = plan.PageDocumentIdSql.IndexOf(
            "AND (((r.\"SchoolId\"",
            StringComparison.Ordinal
        );

        namespaceIndex.Should().BeGreaterThan(-1);
        relationshipGroupIndex.Should().BeGreaterThan(namespaceIndex);
    }

    [Test]
    public void It_emits_namespace_AND_group_outside_relationship_OR_group_in_mixed_strategy_configs_mssql()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);

        var plan = compiler.Compile(
            CreateMixedSpec(
                SqlDialect.Mssql,
                namespacePrefixes: ["uri://ed-fi.org/", "uri://gbisd.edu/"],
                claimEducationOrganizationIds: [255901L]
            )
        );

        plan.PageDocumentIdSql.Should()
            .Contain(
                "(r.[Namespace] IS NOT NULL AND ("
                    + "r.[Namespace] LIKE @namespacePrefixes_0 ESCAPE '\\' "
                    + "OR r.[Namespace] LIKE @namespacePrefixes_1 ESCAPE '\\'"
                    + "))"
            );
        plan.PageDocumentIdSql.Should().Contain("AND (((r.[SchoolId]");
    }

    [Test]
    public void It_brackets_the_relationship_OR_group_inside_its_own_parens_so_namespace_AND_does_not_flatten_it()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateMixedSpec(
                SqlDialect.Pgsql,
                namespacePrefixes: ["uri://ed-fi.org/"],
                claimEducationOrganizationIds: [255901L, 100L],
                includeInvertedRelationshipStrategy: true
            )
        );

        // The relationship OR group must always appear as one outer-parens predicate, with each
        // OR strategy in its own inner parens. If the compiler ever flattened the group so that
        // the namespace AND associated with the first OR strategy alone, the SQL would read
        // "AND (strat1) OR (strat2)" without the outer wrapper — these two assertions both fail
        // in that regression.
        plan.PageDocumentIdSql.Should().Contain("AND ((");
        plan.PageDocumentIdSql.Should().Contain(") OR (");
    }

    [Test]
    public void It_throws_when_a_namespace_check_spec_targets_a_table_other_than_the_query_root_table()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);
        var spec = new PageDocumentIdQuerySpec(
            RootTable: _rootTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                NamespaceChecks:
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Stored,
                        new DbTableName(_edfiSchema, "OtherRootTable"),
                        _namespaceColumn
                    ),
                ],
                NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                )
            )
        );

        var act = () => compiler.Compile(spec);

        act.Should().Throw<InvalidOperationException>().WithMessage("*root-table*");
    }

    [Test]
    public void It_throws_when_a_namespace_check_entry_is_null()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);
        var spec = new PageDocumentIdQuerySpec(
            RootTable: _rootTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                NamespaceChecks:
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Stored,
                        _rootTable,
                        _namespaceColumn
                    ),
                    null!,
                ],
                NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                )
            )
        );

        var act = () => compiler.Compile(spec);

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*NamespaceChecks must not contain null entries*");
    }

    [Test]
    public void It_throws_rather_than_compiling_without_authorization_when_namespace_checks_contains_only_null()
    {
        // A namespace check list whose only entry is null must fail closed. If the compiler silently
        // dropped the null entry, the spec would normalize to no namespace checks and no strategies,
        // emitting an unauthorized query that returns every row.
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);
        var spec = new PageDocumentIdQuerySpec(
            RootTable: _rootTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                NamespaceChecks: [null!],
                NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                )
            )
        );

        var act = () => compiler.Compile(spec);

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*NamespaceChecks must not contain null entries*");
    }

    [Test]
    public void It_escapes_like_metacharacters_in_pgsql_prefixes_so_underscore_and_percent_match_literally()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(CreateNamespaceOnlySpec(SqlDialect.Pgsql, ["uri://a_b/", "uri://c%d/"]));

        // The escaped prefixes are bound parameter VALUES, not part of the SQL text, so assert
        // through the parameterization the spec carries. The trailing % stays an unescaped wildcard.
        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Pgsql,
            ["uri://a_b/", "uri://c%d/"],
            "namespacePrefixes"
        );
        parameterization.LikePatternsInOrder.Should().Equal("uri://a\\_b/%", "uri://c\\%d/%");
        plan.PageDocumentIdSql.Should().Contain("LIKE ANY(@namespacePrefixes)");
        plan.PageDocumentIdSql.Should().NotContain("ESCAPE");
    }

    [Test]
    public void It_escapes_like_metacharacters_in_mssql_prefixes_and_emits_an_escape_clause()
    {
        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            ["uri://a_b/", "uri://c%d/", "uri://e[f/"],
            "namespacePrefixes"
        );

        parameterization
            .LikePatternsInOrder.Should()
            .Equal("uri://a\\_b/%", "uri://c\\%d/%", "uri://e\\[f/%");

        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);
        var plan = compiler.Compile(
            CreateNamespaceOnlySpec(SqlDialect.Mssql, ["uri://a_b/", "uri://c%d/", "uri://e[f/"])
        );

        plan.PageDocumentIdSql.Should().Contain("LIKE @namespacePrefixes_0 ESCAPE '\\'");
    }

    [Test]
    public void It_throws_when_a_pgsql_array_parameterization_is_handed_to_a_mssql_compiler()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);
        var spec = new PageDocumentIdQuerySpec(
            RootTable: _rootTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                NamespaceChecks:
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Stored,
                        _rootTable,
                        _namespaceColumn
                    ),
                ],
                NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                )
            )
        );

        var act = () => compiler.Compile(spec);

        act.Should().Throw<ArgumentException>().WithMessage("*not supported by SQL dialect 'Mssql'*");
    }

    [Test]
    public void It_throws_when_a_mssql_scalar_parameterization_is_handed_to_a_pgsql_compiler()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);
        var spec = new PageDocumentIdQuerySpec(
            RootTable: _rootTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                NamespaceChecks:
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Stored,
                        _rootTable,
                        _namespaceColumn
                    ),
                ],
                NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Mssql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                )
            )
        );

        var act = () => compiler.Compile(spec);

        act.Should().Throw<ArgumentException>().WithMessage("*not supported by SQL dialect 'Pgsql'*");
    }

    [Test]
    public void It_emits_namespace_AND_group_before_the_relationship_OR_group_in_the_WHERE_clause_order()
    {
        // Namespace AND group is emitted before the relationship OR group so the composed WHERE
        // clause is deterministic and the AND-around-OR bracketing reads top-to-bottom.
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateMixedSpec(
                SqlDialect.Pgsql,
                namespacePrefixes: ["uri://ed-fi.org/"],
                claimEducationOrganizationIds: [255901L]
            )
        );

        var namespaceIndex = plan.PageDocumentIdSql.IndexOf("Namespace", StringComparison.Ordinal);
        var schoolIdIndex = plan.PageDocumentIdSql.IndexOf("SchoolId", StringComparison.Ordinal);

        namespaceIndex.Should().BeLessThan(schoolIdIndex);
    }

    [Test]
    public void It_produces_the_total_count_sql_with_the_same_namespace_predicate_shape()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateNamespaceOnlySpec(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/", "uri://gbisd.edu/"],
                includeTotalCountSql: true
            )
        );

        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql!.Should()
            .Contain("(r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes))");
    }

    [Test]
    public void It_emits_a_pgsql_descriptor_alias_LIKE_ANY_predicate_when_the_namespace_check_targets_dms_Descriptor()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateDescriptorNamespaceOnlySpec(SqlDialect.Pgsql, ["uri://ed-fi.org/", "uri://gbisd.edu/"])
        );

        plan.PageDocumentIdSql.Should()
            .Contain("(d.\"Namespace\" IS NOT NULL AND d.\"Namespace\" LIKE ANY(@namespacePrefixes))");
        plan.PageDocumentIdSql.Should().NotContain("r.\"Namespace\"");
    }

    [Test]
    public void It_emits_a_mssql_descriptor_alias_OR_chain_LIKE_predicate_when_the_namespace_check_targets_dms_Descriptor()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);

        var plan = compiler.Compile(
            CreateDescriptorNamespaceOnlySpec(SqlDialect.Mssql, ["uri://ed-fi.org/", "uri://gbisd.edu/"])
        );

        plan.PageDocumentIdSql.Should()
            .Contain(
                "(d.[Namespace] IS NOT NULL AND ("
                    + "d.[Namespace] LIKE @namespacePrefixes_0 ESCAPE '\\' "
                    + "OR d.[Namespace] LIKE @namespacePrefixes_1 ESCAPE '\\'"
                    + "))"
            );
        plan.PageDocumentIdSql.Should().NotContain("r.[Namespace]");
    }

    [Test]
    public void It_triggers_the_descriptor_join_when_only_a_descriptor_namespace_check_is_present()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateDescriptorNamespaceOnlySpec(SqlDialect.Pgsql, ["uri://ed-fi.org/"])
        );

        plan.PageDocumentIdSql.Should()
            .Contain("INNER JOIN \"dms\".\"Descriptor\" d ON d.\"DocumentId\" = r.\"DocumentId\"");
    }

    [Test]
    public void It_triggers_the_descriptor_join_in_total_count_sql_when_only_a_descriptor_namespace_check_is_present()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateDescriptorNamespaceOnlySpec(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/"],
                includeTotalCountSql: true
            )
        );

        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql!.Should()
            .Contain("INNER JOIN \"dms\".\"Descriptor\" d ON d.\"DocumentId\" = r.\"DocumentId\"");
        plan.TotalCountSql.Should()
            .Contain("(d.\"Namespace\" IS NOT NULL AND d.\"Namespace\" LIKE ANY(@namespacePrefixes))");
    }

    [Test]
    public void It_keeps_the_document_alias_for_root_predicates_when_a_descriptor_namespace_check_is_emitted()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateDescriptorSpecWithResourceKeyAndNamespace(SqlDialect.Pgsql, ["uri://ed-fi.org/"])
        );

        plan.PageDocumentIdSql.Should().Contain("r.\"ResourceKeyId\" = @resourceKeyId");
        plan.PageDocumentIdSql.Should()
            .Contain("(d.\"Namespace\" IS NOT NULL AND d.\"Namespace\" LIKE ANY(@namespacePrefixes))");
    }

    [Test]
    public void It_brackets_the_descriptor_namespace_AND_group_alongside_a_descriptor_column_predicate()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateDescriptorSpecWithCodeValueAndNamespace(SqlDialect.Pgsql, ["uri://ed-fi.org/"])
        );

        plan.PageDocumentIdSql.Should().Contain("d.\"CodeValue\" = @codeValue");
        plan.PageDocumentIdSql.Should()
            .Contain("(d.\"Namespace\" IS NOT NULL AND d.\"Namespace\" LIKE ANY(@namespacePrefixes))");
        plan.PageDocumentIdSql.Should()
            .Contain("INNER JOIN \"dms\".\"Descriptor\" d ON d.\"DocumentId\" = r.\"DocumentId\"");
    }

    [Test]
    public void It_lists_descriptor_namespace_prefix_parameters_in_the_page_parameter_inventory()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateDescriptorNamespaceOnlySpec(SqlDialect.Pgsql, ["uri://ed-fi.org/", "uri://gbisd.edu/"])
        );

        plan.PageParametersInOrder.Select(static p => p.ParameterName)
            .Should()
            .Equal("namespacePrefixes", "offset", "limit");
        plan.PageParametersInOrder.First(static p => p.ParameterName == "namespacePrefixes")
            .Binding.Kind.Should()
            .Be(QuerySqlParameterBindingKind.PgsqlArray);
    }

    [Test]
    public void It_binds_the_pgsql_namespace_check_to_the_root_alias_without_a_self_join_when_the_query_roots_on_dms_Descriptor()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(
            CreateDescriptorRootNamespaceOnlySpec(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/", "uri://gbisd.edu/"],
                includeTotalCountSql: true
            )
        );

        plan.PageDocumentIdSql.Should().Contain("FROM \"dms\".\"Descriptor\" r");
        plan.PageDocumentIdSql.Should().NotContain("INNER JOIN");
        plan.PageDocumentIdSql.Should()
            .Contain("(r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes))");
        plan.TotalCountSql.Should().NotBeNull();
        plan.TotalCountSql!.Should().NotContain("INNER JOIN");
        plan.TotalCountSql.Should()
            .Contain("(r.\"Namespace\" IS NOT NULL AND r.\"Namespace\" LIKE ANY(@namespacePrefixes))");
    }

    [Test]
    public void It_binds_the_mssql_namespace_check_to_the_root_alias_without_a_self_join_when_the_query_roots_on_dms_Descriptor()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Mssql);

        var plan = compiler.Compile(
            CreateDescriptorRootNamespaceOnlySpec(SqlDialect.Mssql, ["uri://ed-fi.org/", "uri://gbisd.edu/"])
        );

        plan.PageDocumentIdSql.Should().Contain("FROM [dms].[Descriptor] r");
        plan.PageDocumentIdSql.Should().NotContain("INNER JOIN");
        plan.PageDocumentIdSql.Should()
            .Contain(
                "(r.[Namespace] IS NOT NULL AND ("
                    + "r.[Namespace] LIKE @namespacePrefixes_0 ESCAPE '\\' "
                    + "OR r.[Namespace] LIKE @namespacePrefixes_1 ESCAPE '\\'"
                    + "))"
            );
    }

    [Test]
    public void It_still_throws_when_a_namespace_check_targets_a_table_that_is_neither_the_query_root_nor_dms_Descriptor()
    {
        var compiler = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql);
        var spec = new PageDocumentIdQuerySpec(
            RootTable: _documentTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                NamespaceChecks:
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Stored,
                        new DbTableName(_edfiSchema, "SomeOtherTable"),
                        _namespaceColumn
                    ),
                ],
                NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://ed-fi.org/"],
                    "namespacePrefixes"
                )
            )
        );

        var act = () => compiler.Compile(spec);

        act.Should().Throw<InvalidOperationException>();
    }

    private static PageDocumentIdQuerySpec CreateDescriptorNamespaceOnlySpec(
        SqlDialect dialect,
        IReadOnlyList<string> namespacePrefixes,
        bool includeTotalCountSql = false
    ) =>
        new(
            RootTable: _documentTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            IncludeTotalCountSql: includeTotalCountSql,
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                NamespaceChecks:
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Stored,
                        _descriptorTable,
                        _namespaceColumn
                    ),
                ],
                NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                    dialect,
                    namespacePrefixes,
                    "namespacePrefixes"
                )
            )
        );

    private static PageDocumentIdQuerySpec CreateDescriptorRootNamespaceOnlySpec(
        SqlDialect dialect,
        IReadOnlyList<string> namespacePrefixes,
        bool includeTotalCountSql = false
    ) =>
        new(
            RootTable: _descriptorTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            IncludeTotalCountSql: includeTotalCountSql,
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                NamespaceChecks:
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Stored,
                        _descriptorTable,
                        _namespaceColumn
                    ),
                ],
                NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                    dialect,
                    namespacePrefixes,
                    "namespacePrefixes"
                )
            )
        );

    private static PageDocumentIdQuerySpec CreateDescriptorSpecWithResourceKeyAndNamespace(
        SqlDialect dialect,
        IReadOnlyList<string> namespacePrefixes
    ) =>
        new(
            RootTable: _documentTable,
            Predicates:
            [
                new QueryValuePredicate(
                    new DbColumnName("ResourceKeyId"),
                    QueryComparisonOperator.Equal,
                    "resourceKeyId"
                ),
            ],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                NamespaceChecks:
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Stored,
                        _descriptorTable,
                        _namespaceColumn
                    ),
                ],
                NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                    dialect,
                    namespacePrefixes,
                    "namespacePrefixes"
                )
            )
        );

    private static PageDocumentIdQuerySpec CreateDescriptorSpecWithCodeValueAndNamespace(
        SqlDialect dialect,
        IReadOnlyList<string> namespacePrefixes
    ) =>
        new(
            RootTable: _documentTable,
            Predicates:
            [
                new QueryValuePredicate(
                    new DbColumnName("ResourceKeyId"),
                    QueryComparisonOperator.Equal,
                    "resourceKeyId"
                ),
                new QueryValuePredicate(
                    new QueryPredicateTarget.DescriptorColumn(new DbColumnName("CodeValue")),
                    QueryComparisonOperator.Equal,
                    "codeValue",
                    ScalarKind.String
                ),
            ],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                NamespaceChecks:
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Stored,
                        _descriptorTable,
                        _namespaceColumn
                    ),
                ],
                NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                    dialect,
                    namespacePrefixes,
                    "namespacePrefixes"
                )
            )
        );

    private static PageDocumentIdQuerySpec CreateNamespaceOnlySpec(
        SqlDialect dialect,
        IReadOnlyList<string> namespacePrefixes,
        bool includeTotalCountSql = false
    ) =>
        new(
            RootTable: _rootTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            IncludeTotalCountSql: includeTotalCountSql,
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: [],
                NamespaceChecks:
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Stored,
                        _rootTable,
                        _namespaceColumn
                    ),
                ],
                NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                    dialect,
                    namespacePrefixes,
                    "namespacePrefixes"
                )
            )
        );

    private static PageDocumentIdAuthorizationEdOrgSubject CreateEdOrgSchoolSubject(
        RelationshipAuthorizationHierarchyDirection direction
    ) =>
        new(
            _rootTable,
            new DbColumnName("SchoolId"),
            RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(direction),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.EducationOrganization,
                    "$.SchoolId",
                    "SchoolId"
                ),
            ]
        );

    private static PageDocumentIdQuerySpec CreateMixedSpec(
        SqlDialect dialect,
        IReadOnlyList<string> namespacePrefixes,
        IReadOnlyList<long> claimEducationOrganizationIds,
        bool includeInvertedRelationshipStrategy = false
    )
    {
        var strategies = new List<PageDocumentIdAuthorizationStrategy>
        {
            new(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                [CreateEdOrgSchoolSubject(RelationshipAuthorizationHierarchyDirection.Normal)]
            ),
        };

        if (includeInvertedRelationshipStrategy)
        {
            strategies.Add(
                new PageDocumentIdAuthorizationStrategy(
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
                    [CreateEdOrgSchoolSubject(RelationshipAuthorizationHierarchyDirection.Inverted)]
                )
            );
        }

        return new PageDocumentIdQuerySpec(
            RootTable: _rootTable,
            Predicates: [],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Authorization: new PageDocumentIdAuthorizationSpec(
                Strategies: strategies,
                ClaimEducationOrganizationIdParameterization: AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                    dialect,
                    claimEducationOrganizationIds,
                    RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
                ),
                NamespaceChecks:
                [
                    new NamespaceAuthorizationCheckSpec(
                        0,
                        NamespaceAuthorizationCheckValueSource.Stored,
                        _rootTable,
                        _namespaceColumn
                    ),
                ],
                NamespacePrefixParameterization: NamespacePrefixParameterizationFactory.Create(
                    dialect,
                    namespacePrefixes,
                    "namespacePrefixes"
                )
            )
        );
    }
}
