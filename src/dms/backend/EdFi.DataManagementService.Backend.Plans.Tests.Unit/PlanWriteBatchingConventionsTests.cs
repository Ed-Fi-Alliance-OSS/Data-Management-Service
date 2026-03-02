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
public class Given_PlanWriteBatchingConventions
{
    [TestCase(SqlDialect.Mssql, 1, 1000, 2100)]
    [TestCase(SqlDialect.Mssql, 3, 700, 2100)]
    [TestCase(SqlDialect.Mssql, 2100, 1, 2100)]
    [TestCase(SqlDialect.Pgsql, 1, 1000, 65535)]
    [TestCase(SqlDialect.Pgsql, 100, 655, 65535)]
    [TestCase(SqlDialect.Pgsql, 65535, 1, 65535)]
    public void It_should_compute_dialect_aware_batching_info(
        SqlDialect dialect,
        int parametersPerRow,
        int expectedMaxRowsPerBatch,
        int expectedMaxParametersPerCommand
    )
    {
        var batchingInfo = PlanWriteBatchingConventions.DeriveBulkInsertBatchingInfo(
            dialect,
            parametersPerRow
        );

        batchingInfo.MaxRowsPerBatch.Should().Be(expectedMaxRowsPerBatch);
        batchingInfo.ParametersPerRow.Should().Be(parametersPerRow);
        batchingInfo.MaxParametersPerCommand.Should().Be(expectedMaxParametersPerCommand);
    }

    [TestCase(SqlDialect.Mssql, 0)]
    [TestCase(SqlDialect.Pgsql, -1)]
    public void It_should_fail_fast_when_parameters_per_row_is_less_than_one(
        SqlDialect dialect,
        int parametersPerRow
    )
    {
        var act = () => PlanWriteBatchingConventions.DeriveBulkInsertBatchingInfo(dialect, parametersPerRow);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestCase(SqlDialect.Mssql, 2101)]
    [TestCase(SqlDialect.Pgsql, 65536)]
    public void It_should_fail_fast_when_row_width_exceeds_dialect_parameter_limit(
        SqlDialect dialect,
        int parametersPerRow
    )
    {
        var act = () => PlanWriteBatchingConventions.DeriveBulkInsertBatchingInfo(dialect, parametersPerRow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void It_should_compute_parameters_per_row_from_column_binding_count()
    {
        var columnBindings = CreateBindings(count: 4);

        var batchingInfo = PlanWriteBatchingConventions.DeriveBulkInsertBatchingInfo(
            SqlDialect.Mssql,
            columnBindings
        );

        batchingInfo.ParametersPerRow.Should().Be(columnBindings.Count);
        batchingInfo.MaxRowsPerBatch.Should().Be(525);
        batchingInfo.MaxParametersPerCommand.Should().Be(2100);
    }

    private static IReadOnlyList<WriteColumnBinding> CreateBindings(int count)
    {
        var bindings = new WriteColumnBinding[count];

        for (var index = 0; index < count; index++)
        {
            bindings[index] = new WriteColumnBinding(
                Column: new DbColumnModel(
                    new DbColumnName($"Column{index}"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                Source: new WriteValueSource.DocumentId(),
                ParameterName: $"p{index}"
            );
        }

        return bindings;
    }
}
