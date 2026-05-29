// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Verifies that the synthesized change-version mirror columns (<c>ContentVersion</c> and
/// <c>ContentLastModifiedAt</c>) are excluded from client-writable projections and generated DML.
/// They are non-writable stored columns maintained only by document-stamping triggers.
/// </summary>
[TestFixture]
public class Given_WritePlanCompiler_ContentVersionMirrorColumns : WritePlanCompilerTestBase
{
    private static RelationalResourceModel CreateModelWithMirrorColumns()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            Columns =
            [
                .. model.Root.Columns,
                new DbColumnModel(
                    ColumnName: new DbColumnName("ContentVersion"),
                    Kind: ColumnKind.MirroredContentVersion,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                )
                {
                    IsWritable = false,
                },
                new DbColumnModel(
                    ColumnName: new DbColumnName("ContentLastModifiedAt"),
                    Kind: ColumnKind.MirroredContentLastModifiedAt,
                    ScalarType: new RelationalScalarType(ScalarKind.DateTime),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                )
                {
                    IsWritable = false,
                },
            ],
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_exclude_mirror_columns_from_column_bindings(SqlDialect dialect)
    {
        var tablePlan = new WritePlanCompiler(dialect)
            .Compile(CreateModelWithMirrorColumns())
            .TablePlansInDependencyOrder.Single();

        tablePlan
            .ColumnBindings.Select(binding => binding.Column.ColumnName.Value)
            .Should()
            .NotContain("ContentVersion")
            .And.NotContain("ContentLastModifiedAt");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_exclude_mirror_columns_from_insert_and_update_sql(SqlDialect dialect)
    {
        var tablePlan = new WritePlanCompiler(dialect)
            .Compile(CreateModelWithMirrorColumns())
            .TablePlansInDependencyOrder.Single();

        tablePlan.InsertSql.Should().NotContain("ContentVersion").And.NotContain("ContentLastModifiedAt");

        tablePlan.UpdateSql.Should().NotBeNull();
        tablePlan.UpdateSql!.Should().NotContain("ContentVersion").And.NotContain("ContentLastModifiedAt");
    }
}
