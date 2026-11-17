// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// A predicate's declared string contract reaches the parameter inventory of the page, the optional
/// total count and the partition boundary statement, and SQL Server namespace prefix patterns are
/// declared unbounded, so the binder can declare every string parameter without reading its value.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_PageDocumentIdSqlCompiler_String_Contracts
{
    private static readonly DbTableName _rootTable = new(new DbSchemaName("dms"), "Descriptor");
    private static readonly DbColumnName _namespaceColumn = new("Namespace");

    private static PageDocumentIdQuerySpec Spec(SqlDialect dialect, PageCandidateMode mode) =>
        new(
            RootTable: _rootTable,
            Predicates:
            [
                new QueryValuePredicate(
                    new DbColumnName("CodeValue"),
                    QueryComparisonOperator.Equal,
                    "codeValue",
                    new QuerySqlStringContract(50)
                ),
                new QueryValuePredicate(
                    new DbColumnName("Description"),
                    QueryComparisonOperator.Equal,
                    "description",
                    new QuerySqlStringContract(null)
                ),
                new QueryValuePredicate(
                    new DbColumnName("ResourceKeyId"),
                    QueryComparisonOperator.Equal,
                    "resourceKeyId"
                ),
            ],
            UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
            Mode: mode,
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
                    ["uri://ed-fi.org/", "uri://gbisd.edu/"],
                    "namespacePrefixes"
                )
            )
        );

    private static QuerySqlParameterBinding BindingOf(
        IEnumerable<QuerySqlParameter> parameters,
        string parameterName
    ) => parameters.Single(parameter => parameter.ParameterName == parameterName).Binding;

    [Test]
    public void It_carries_each_predicates_string_contract_into_the_page_and_total_count_inventories()
    {
        var plan = new PageDocumentIdSqlCompiler(SqlDialect.Mssql).Compile(
            Spec(SqlDialect.Mssql, new PageCandidateMode.Traditional(IncludeTotalCountSql: true))
        );

        foreach (
            var inventory in new[] { plan.PageParametersInOrder, plan.TotalCountParametersInOrder!.Value }
        )
        {
            BindingOf(inventory, "codeValue").Should().Be(QuerySqlParameterBinding.CreateString(50));
            BindingOf(inventory, "description").Should().Be(QuerySqlParameterBinding.CreateString(null));
            BindingOf(inventory, "resourceKeyId").Should().Be(QuerySqlParameterBinding.Scalar);
        }
    }

    [Test]
    public void It_declares_sql_server_namespace_prefix_patterns_unbounded()
    {
        var plan = new PageDocumentIdSqlCompiler(SqlDialect.Mssql).Compile(
            Spec(SqlDialect.Mssql, new PageCandidateMode.Traditional(IncludeTotalCountSql: true))
        );

        var namespaceParameters = plan
            .PageParametersInOrder.Where(parameter =>
                parameter.ParameterName.StartsWith("namespacePrefixes", StringComparison.Ordinal)
            )
            .ToArray();

        namespaceParameters.Should().HaveCount(2);
        namespaceParameters
            .Select(parameter => parameter.Binding)
            .Should()
            .AllBeEquivalentTo(QuerySqlParameterBinding.CreateString(null));
    }

    [Test]
    public void It_keeps_the_postgresql_namespace_prefix_array_binding()
    {
        var plan = new PageDocumentIdSqlCompiler(SqlDialect.Pgsql).Compile(
            Spec(SqlDialect.Pgsql, new PageCandidateMode.Traditional(IncludeTotalCountSql: false))
        );

        BindingOf(plan.PageParametersInOrder, "namespacePrefixes")
            .Should()
            .Be(QuerySqlParameterBinding.PgsqlArray);
    }

    [Test]
    public void It_carries_the_string_contracts_into_the_partition_boundary_inventory()
    {
        var candidatePlan = new PageDocumentIdSqlCompiler(SqlDialect.Mssql).Compile(
            Spec(SqlDialect.Mssql, new PageCandidateMode.UnpagedCandidates())
        );

        var partitionPlan = new PartitionWindowSqlCompiler(SqlDialect.Mssql).Compile(
            candidatePlan,
            new PageCandidateMode.UnpagedCandidates()
        );

        BindingOf(partitionPlan.PageParametersInOrder, "codeValue")
            .Should()
            .Be(QuerySqlParameterBinding.CreateString(50));
    }
}
