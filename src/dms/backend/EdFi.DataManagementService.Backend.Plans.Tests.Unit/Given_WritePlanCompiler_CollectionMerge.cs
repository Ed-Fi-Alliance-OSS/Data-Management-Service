// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_WritePlanCompiler_CollectionMerge : WritePlanCompilerTestBase
{
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_compile_stable_row_identity_dml_for_top_level_collection_tables(SqlDialect dialect)
    {
        var tablePlan = CompileFocusedStableKeyFixtureTablePlan(dialect, "SchoolAddress");

        AssertCollectionMergePlan(
            tablePlan,
            expectedSemanticIdentityBindings: [("$.city", "City")],
            expectedUpdateByStableRowIdentitySql: dialect switch
            {
                SqlDialect.Pgsql => """
                UPDATE "edfi"."SchoolAddress"
                SET
                    "Ordinal" = @ordinal,
                    "City" = @city
                WHERE
                    ("CollectionItemId" = @collectionItemId)
                ;

                """,
                SqlDialect.Mssql => """
                UPDATE [edfi].[SchoolAddress]
                SET
                    [Ordinal] = @ordinal,
                    [City] = @city
                WHERE
                    ([CollectionItemId] = @collectionItemId)
                ;

                """,
                _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
            },
            expectedDeleteByStableRowIdentitySql: dialect switch
            {
                SqlDialect.Pgsql => """
                DELETE FROM "edfi"."SchoolAddress"
                WHERE
                    ("CollectionItemId" = @collectionItemId)
                ;

                """,
                SqlDialect.Mssql => """
                DELETE FROM [edfi].[SchoolAddress]
                WHERE
                    ([CollectionItemId] = @collectionItemId)
                ;

                """,
                _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
            },
            "Ordinal",
            "City"
        );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_compile_stable_row_identity_dml_for_root_extension_collection_tables(
        SqlDialect dialect
    )
    {
        var tablePlan = CompileFocusedStableKeyFixtureTablePlan(dialect, "SchoolExtensionIntervention");

        AssertCollectionMergePlan(
            tablePlan,
            expectedSemanticIdentityBindings: [("$.interventionCode", "InterventionCode")],
            expectedUpdateByStableRowIdentitySql: dialect switch
            {
                SqlDialect.Pgsql => """
                UPDATE "sample"."SchoolExtensionIntervention"
                SET
                    "Ordinal" = @ordinal,
                    "InterventionCode" = @interventionCode
                WHERE
                    ("CollectionItemId" = @collectionItemId)
                ;

                """,
                SqlDialect.Mssql => """
                UPDATE [sample].[SchoolExtensionIntervention]
                SET
                    [Ordinal] = @ordinal,
                    [InterventionCode] = @interventionCode
                WHERE
                    ([CollectionItemId] = @collectionItemId)
                ;

                """,
                _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
            },
            expectedDeleteByStableRowIdentitySql: dialect switch
            {
                SqlDialect.Pgsql => """
                DELETE FROM "sample"."SchoolExtensionIntervention"
                WHERE
                    ("CollectionItemId" = @collectionItemId)
                ;

                """,
                SqlDialect.Mssql => """
                DELETE FROM [sample].[SchoolExtensionIntervention]
                WHERE
                    ([CollectionItemId] = @collectionItemId)
                ;

                """,
                _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
            },
            "Ordinal",
            "InterventionCode"
        );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_compile_stable_row_identity_dml_for_nested_extension_collection_tables(
        SqlDialect dialect
    )
    {
        var tablePlan = CompileFocusedStableKeyFixtureTablePlan(dialect, "SchoolExtensionInterventionVisit");

        AssertCollectionMergePlan(
            tablePlan,
            expectedSemanticIdentityBindings: [("$.visitCode", "VisitCode")],
            expectedUpdateByStableRowIdentitySql: dialect switch
            {
                SqlDialect.Pgsql => """
                UPDATE "sample"."SchoolExtensionInterventionVisit"
                SET
                    "Ordinal" = @ordinal,
                    "VisitCode" = @visitCode
                WHERE
                    ("CollectionItemId" = @collectionItemId)
                ;

                """,
                SqlDialect.Mssql => """
                UPDATE [sample].[SchoolExtensionInterventionVisit]
                SET
                    [Ordinal] = @ordinal,
                    [VisitCode] = @visitCode
                WHERE
                    ([CollectionItemId] = @collectionItemId)
                ;

                """,
                _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
            },
            expectedDeleteByStableRowIdentitySql: dialect switch
            {
                SqlDialect.Pgsql => """
                DELETE FROM "sample"."SchoolExtensionInterventionVisit"
                WHERE
                    ("CollectionItemId" = @collectionItemId)
                ;

                """,
                SqlDialect.Mssql => """
                DELETE FROM [sample].[SchoolExtensionInterventionVisit]
                WHERE
                    ([CollectionItemId] = @collectionItemId)
                ;

                """,
                _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
            },
            "Ordinal",
            "VisitCode"
        );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_compile_stable_row_identity_dml_for_nested_collection_tables(SqlDialect dialect)
    {
        var tablePlan = CompileFocusedStableKeyFixtureTablePlan(dialect, "SchoolAddressPeriod");

        AssertCollectionMergePlan(
            tablePlan,
            expectedSemanticIdentityBindings: [("$.periodName", "PeriodName")],
            expectedUpdateByStableRowIdentitySql: dialect switch
            {
                SqlDialect.Pgsql => """
                UPDATE "edfi"."SchoolAddressPeriod"
                SET
                    "Ordinal" = @ordinal,
                    "PeriodName" = @periodName
                WHERE
                    ("CollectionItemId" = @collectionItemId)
                ;

                """,
                SqlDialect.Mssql => """
                UPDATE [edfi].[SchoolAddressPeriod]
                SET
                    [Ordinal] = @ordinal,
                    [PeriodName] = @periodName
                WHERE
                    ([CollectionItemId] = @collectionItemId)
                ;

                """,
                _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
            },
            expectedDeleteByStableRowIdentitySql: dialect switch
            {
                SqlDialect.Pgsql => """
                DELETE FROM "edfi"."SchoolAddressPeriod"
                WHERE
                    ("CollectionItemId" = @collectionItemId)
                ;

                """,
                SqlDialect.Mssql => """
                DELETE FROM [edfi].[SchoolAddressPeriod]
                WHERE
                    ([CollectionItemId] = @collectionItemId)
                ;

                """,
                _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
            },
            "Ordinal",
            "PeriodName"
        );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_compile_stable_row_identity_dml_for_collection_aligned_extension_child_collections(
        SqlDialect dialect
    )
    {
        var tablePlan = CompileFocusedStableKeyFixtureTablePlan(
            dialect,
            "SchoolExtensionAddressSponsorReference"
        );

        AssertCollectionMergePlan(
            tablePlan,
            expectedSemanticIdentityBindings: [("$.programReference.programName", "Program_DocumentId")],
            expectedUpdateByStableRowIdentitySql: dialect switch
            {
                SqlDialect.Pgsql => """
                UPDATE "sample"."SchoolExtensionAddressSponsorReference"
                SET
                    "Ordinal" = @ordinal,
                    "Program_DocumentId" = @program_DocumentId,
                    "Program_ProgramName" = @program_ProgramName
                WHERE
                    ("CollectionItemId" = @collectionItemId)
                ;

                """,
                SqlDialect.Mssql => """
                UPDATE [sample].[SchoolExtensionAddressSponsorReference]
                SET
                    [Ordinal] = @ordinal,
                    [Program_DocumentId] = @program_DocumentId,
                    [Program_ProgramName] = @program_ProgramName
                WHERE
                    ([CollectionItemId] = @collectionItemId)
                ;

                """,
                _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
            },
            expectedDeleteByStableRowIdentitySql: dialect switch
            {
                SqlDialect.Pgsql => """
                DELETE FROM "sample"."SchoolExtensionAddressSponsorReference"
                WHERE
                    ("CollectionItemId" = @collectionItemId)
                ;

                """,
                SqlDialect.Mssql => """
                DELETE FROM [sample].[SchoolExtensionAddressSponsorReference]
                WHERE
                    ([CollectionItemId] = @collectionItemId)
                ;

                """,
                _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
            },
            "Ordinal",
            "Program_DocumentId",
            "Program_ProgramName"
        );
    }

    private static void AssertCollectionMergePlan(
        TableWritePlan tablePlan,
        IReadOnlyList<(string RelativePath, string ColumnName)> expectedSemanticIdentityBindings,
        string expectedUpdateByStableRowIdentitySql,
        string expectedDeleteByStableRowIdentitySql,
        params string[] expectedCompareColumnsInOrder
    )
    {
        tablePlan.UpdateSql.Should().BeNull();
        tablePlan.DeleteByParentSql.Should().BeNull();
        tablePlan.CollectionMergePlan.Should().NotBeNull();
        tablePlan.CollectionKeyPreallocationPlan.Should().NotBeNull();

        var collectionMergePlan = tablePlan.CollectionMergePlan!;

        collectionMergePlan
            .SemanticIdentityBindings.Select(binding =>
                (
                    binding.RelativePath.Canonical,
                    tablePlan.ColumnBindings[binding.BindingIndex].Column.ColumnName.Value
                )
            )
            .Should()
            .Equal(expectedSemanticIdentityBindings);

        collectionMergePlan.UpdateByStableRowIdentitySql.Should().Be(expectedUpdateByStableRowIdentitySql);
        collectionMergePlan.DeleteByStableRowIdentitySql.Should().Be(expectedDeleteByStableRowIdentitySql);

        tablePlan
            .ColumnBindings[collectionMergePlan.StableRowIdentityBindingIndex]
            .Column.ColumnName.Value.Should()
            .Be("CollectionItemId");
        tablePlan
            .CollectionKeyPreallocationPlan!.BindingIndex.Should()
            .Be(collectionMergePlan.StableRowIdentityBindingIndex);
        tablePlan
            .ColumnBindings[collectionMergePlan.OrdinalBindingIndex]
            .Column.ColumnName.Value.Should()
            .Be("Ordinal");
        collectionMergePlan
            .CompareBindingIndexesInOrder.Should()
            .NotContain(collectionMergePlan.StableRowIdentityBindingIndex);
        collectionMergePlan
            .CompareBindingIndexesInOrder.Select(bindingIndex =>
                tablePlan.ColumnBindings[bindingIndex].Column.ColumnName.Value
            )
            .Should()
            .Equal(expectedCompareColumnsInOrder);
    }
}
