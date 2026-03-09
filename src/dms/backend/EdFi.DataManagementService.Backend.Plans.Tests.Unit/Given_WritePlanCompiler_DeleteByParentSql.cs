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
public class Given_WritePlanCompiler_DeleteByParentSql : WritePlanCompilerTestBase
{
    [Test]
    public void It_should_compile_table_plans_for_all_tables_in_dependency_order_for_multi_table_resources()
    {
        var model = CreateSupportedMultiTableModel();
        var writePlan = new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);

        writePlan.TablePlansInDependencyOrder.Should().HaveCount(model.TablesInDependencyOrder.Count);
        writePlan
            .TablePlansInDependencyOrder.Select(static tablePlan => tablePlan.TableModel.Table)
            .Should()
            .Equal(model.TablesInDependencyOrder.Select(static table => table.Table));

        var rootPlan = writePlan.TablePlansInDependencyOrder[0];
        var rootExtensionPlan = writePlan.TablePlansInDependencyOrder[1];
        var childPlan = writePlan.TablePlansInDependencyOrder[2];
        var nestedChildPlan = writePlan.TablePlansInDependencyOrder[3];

        rootPlan.UpdateSql.Should().NotBeNull();
        rootExtensionPlan.UpdateSql.Should().NotBeNull();
        childPlan.UpdateSql.Should().BeNull();
        nestedChildPlan.UpdateSql.Should().BeNull();

        rootPlan.DeleteByParentSql.Should().BeNull();
        rootExtensionPlan.DeleteByParentSql.Should().NotBeNull();
        childPlan.DeleteByParentSql.Should().NotBeNull();
        nestedChildPlan.DeleteByParentSql.Should().NotBeNull();

        foreach (var tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            tablePlan.InsertSql.Should().NotBeNullOrWhiteSpace();
            tablePlan.BulkInsertBatching.ParametersPerRow.Should().Be(tablePlan.ColumnBindings.Length);
            tablePlan.KeyUnificationPlans.Should().BeEmpty();
        }
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_emit_delete_by_parent_sql_for_direct_child_nested_child_and_root_scope_extension_tables(
        SqlDialect dialect
    )
    {
        var writePlan = new WritePlanCompiler(dialect).Compile(CreateSupportedMultiTableModel());

        static TableWritePlan GetTablePlan(ResourceWritePlan plan, string tableName)
        {
            return plan.TablePlansInDependencyOrder.Single(tablePlan =>
                string.Equals(tablePlan.TableModel.Table.Name, tableName, StringComparison.Ordinal)
            );
        }

        var rootPlan = GetTablePlan(writePlan, "Student");
        var rootExtensionPlan = GetTablePlan(writePlan, "StudentExtension");
        var directChildPlan = GetTablePlan(writePlan, "StudentAddress");
        var nestedChildPlan = GetTablePlan(writePlan, "StudentAddressPeriod");

        rootPlan.DeleteByParentSql.Should().BeNull();

        rootExtensionPlan
            .DeleteByParentSql.Should()
            .Be(
                dialect switch
                {
                    SqlDialect.Pgsql => """
                    DELETE FROM "sample"."StudentExtension"
                    WHERE
                        ("DocumentId" = @documentId)
                    ;

                    """,
                    SqlDialect.Mssql => """
                    DELETE FROM [sample].[StudentExtension]
                    WHERE
                        ([DocumentId] = @documentId)
                    ;

                    """,
                    _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
                }
            );

        directChildPlan
            .DeleteByParentSql.Should()
            .Be(
                dialect switch
                {
                    SqlDialect.Pgsql => """
                    DELETE FROM "edfi"."StudentAddress"
                    WHERE
                        ("DocumentId" = @documentId)
                    ;

                    """,
                    SqlDialect.Mssql => """
                    DELETE FROM [edfi].[StudentAddress]
                    WHERE
                        ([DocumentId] = @documentId)
                    ;

                    """,
                    _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
                }
            );

        nestedChildPlan
            .DeleteByParentSql.Should()
            .Be(
                dialect switch
                {
                    SqlDialect.Pgsql => """
                    DELETE FROM "edfi"."StudentAddressPeriod"
                    WHERE
                        ("DocumentId" = @documentId)
                        AND ("ParentAddressOrdinal" = @parentAddressOrdinal)
                    ;

                    """,
                    SqlDialect.Mssql => """
                    DELETE FROM [edfi].[StudentAddressPeriod]
                    WHERE
                        ([DocumentId] = @documentId)
                        AND ([ParentAddressOrdinal] = @parentAddressOrdinal)
                    ;

                    """,
                    _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
                }
            );
    }

    [Test]
    public void It_should_treat_root_scope_table_as_root_for_delete_by_parent_when_root_table_instance_differs()
    {
        var model = CreateSupportedMultiTableModelWithClonedRootScopeTableInDependencyOrder();
        model.TablesInDependencyOrder[0].Equals(model.Root).Should().BeFalse();

        var writePlan = new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);
        var rootPlan = writePlan.TablePlansInDependencyOrder.Single(tablePlan =>
            tablePlan.TableModel.Table.Equals(model.Root.Table)
            && tablePlan.TableModel.JsonScope.Canonical == "$"
        );

        rootPlan.DeleteByParentSql.Should().BeNull();

        writePlan
            .TablePlansInDependencyOrder.Where(tablePlan => tablePlan.TableModel.JsonScope.Canonical != "$")
            .Should()
            .AllSatisfy(tablePlan => tablePlan.DeleteByParentSql.Should().NotBeNull());
    }

    [Test]
    public void It_should_fail_fast_when_tables_in_dependency_order_has_no_root_scope_table()
    {
        var unsupportedModel = CreateSupportedMultiTableModelWithoutRootScopeTable();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for resource 'Ed-Fi.Student': expected exactly one root-scope table (JsonScope '$') in TablesInDependencyOrder, but found 0."
            );
    }

    [Test]
    public void It_should_fail_fast_when_tables_in_dependency_order_has_multiple_root_scope_tables()
    {
        var unsupportedModel = CreateSupportedMultiTableModelWithMultipleRootScopeTables();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for resource 'Ed-Fi.Student': expected exactly one root-scope table (JsonScope '$') in TablesInDependencyOrder, but found 2."
            );
    }

    [Test]
    public void It_should_fail_fast_when_root_scope_table_does_not_match_resource_model_root_table()
    {
        var unsupportedModel = CreateSupportedMultiTableModelWithMismatchedRootScopeTable();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for resource 'Ed-Fi.Student': root-scope table 'edfi.StudentShadow' does not match resourceModel.Root table 'edfi.Student'."
            );
    }

    [Test]
    public void It_should_compile_deterministic_table_plans_for_multi_table_resources_under_unified_alias_column_permutations()
    {
        var compiler = new WritePlanCompiler(SqlDialect.Pgsql);

        var first = compiler.Compile(CreateSupportedMultiTableModel());
        var second = compiler.Compile(CreateSupportedMultiTableModel());
        var permuted = compiler.Compile(CreateSupportedMultiTableModelWithUnifiedAliasColumnsFirst());

        var firstFingerprint = CreateWritePlanFingerprint(first);
        var secondFingerprint = CreateWritePlanFingerprint(second);
        var permutedFingerprint = CreateWritePlanFingerprint(permuted);

        secondFingerprint.Should().Be(firstFingerprint);
        permutedFingerprint.Should().Be(firstFingerprint);
    }

    [Test]
    public void It_should_compile_multi_table_resources_without_thin_slice_gating()
    {
        var multiTableModel = CreateSupportedMultiTableModel();
        multiTableModel.TablesInDependencyOrder.Count.Should().BeGreaterThan(1);

        var writePlan = new WritePlanCompiler(SqlDialect.Pgsql).Compile(multiTableModel);

        writePlan
            .TablePlansInDependencyOrder.Should()
            .HaveCount(multiTableModel.TablesInDependencyOrder.Count);
    }
}
