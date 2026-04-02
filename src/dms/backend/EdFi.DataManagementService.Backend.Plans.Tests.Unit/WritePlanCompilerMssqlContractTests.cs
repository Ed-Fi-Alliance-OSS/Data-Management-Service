// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_Write_Plan_Compiler_For_Mssql_Contract_Safety_Net : WritePlanCompilerTestBase
{
    [Test]
    public void It_emits_bracket_quoted_collection_merge_sql_and_compare_metadata_for_a_base_collection()
    {
        var tablePlan = CompileFocusedStableKeyFixtureTablePlan(SqlDialect.Mssql, "SchoolAddress");
        var collectionMergePlan = tablePlan.CollectionMergePlan;

        collectionMergePlan.Should().NotBeNull();
        tablePlan.InsertSql.Should().Contain("INSERT INTO [edfi].[SchoolAddress]");
        collectionMergePlan!.UpdateByStableRowIdentitySql.Should().Contain("UPDATE [edfi].[SchoolAddress]");
        collectionMergePlan
            .DeleteByStableRowIdentitySql.Should()
            .Contain("DELETE FROM [edfi].[SchoolAddress]");
        tablePlan.CollectionKeyPreallocationPlan.Should().NotBeNull();
        tablePlan
            .ColumnBindings[tablePlan.CollectionKeyPreallocationPlan!.BindingIndex]
            .ParameterName.Should()
            .Be("collectionItemId");

        var compareParameterNames = collectionMergePlan
            .CompareBindingIndexesInOrder.Select(bindingIndex =>
                tablePlan.ColumnBindings[bindingIndex].ParameterName
            )
            .ToArray();

        compareParameterNames.Should().Equal("ordinal", "city");
    }

    [Test]
    public void It_derives_sql_server_batching_limits_and_excludes_locator_columns_for_collection_aligned_extension_children()
    {
        var tablePlan = CompileFocusedStableKeyFixtureTablePlan(
            SqlDialect.Mssql,
            "SchoolExtensionAddressSponsorReference"
        );
        var collectionMergePlan = tablePlan.CollectionMergePlan;

        collectionMergePlan.Should().NotBeNull();
        tablePlan.InsertSql.Should().Contain("INSERT INTO [sample].[SchoolExtensionAddressSponsorReference]");
        collectionMergePlan!
            .UpdateByStableRowIdentitySql.Should()
            .Contain("UPDATE [sample].[SchoolExtensionAddressSponsorReference]");
        tablePlan.BulkInsertBatching.ParametersPerRow.Should().Be(tablePlan.ColumnBindings.Length);
        tablePlan.BulkInsertBatching.MaxParametersPerCommand.Should().Be(2100);
        tablePlan
            .BulkInsertBatching.MaxRowsPerBatch.Should()
            .Be(tablePlan.BulkInsertBatching.MaxParametersPerCommand / tablePlan.ColumnBindings.Length);

        var compareParameterNames = collectionMergePlan
            .CompareBindingIndexesInOrder.Select(bindingIndex =>
                tablePlan.ColumnBindings[bindingIndex].ParameterName
            )
            .ToArray();

        compareParameterNames.Should().Equal("ordinal", "program_DocumentId", "program_ProgramName");
    }
}
