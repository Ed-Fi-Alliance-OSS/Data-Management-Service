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
public class Given_WritePlanCompiler_Batching : WritePlanCompilerTestBase
{
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_derive_bulk_insert_batching_for_each_compiled_table_plan(SqlDialect dialect)
    {
        var writePlan = new WritePlanCompiler(dialect).Compile(CreateSupportedMultiTableModel());

        foreach (var tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            var expectedBatching = PlanWriteBatchingConventions.DeriveBulkInsertBatchingInfo(
                dialect,
                tablePlan.ColumnBindings
            );

            tablePlan.BulkInsertBatching.Should().BeEquivalentTo(expectedBatching);
        }
    }
}
