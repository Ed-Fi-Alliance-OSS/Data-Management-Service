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
    [TestCase(SqlDialect.Mssql, 1, 1000, 2098)]
    [TestCase(SqlDialect.Mssql, 3, 699, 2098)]
    [TestCase(SqlDialect.Mssql, 2098, 1, 2098)]
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

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithMessage("Parameters per row must be at least 1. (Parameter 'parametersPerRow')*")
            .WithParameterName("parametersPerRow");
    }

    [TestCase(
        SqlDialect.Mssql,
        2099,
        "Cannot derive bulk-insert batch size for dialect 'Mssql'. Row width 2099 exceeds max parameters per command (2098)."
    )]
    [TestCase(
        SqlDialect.Pgsql,
        65536,
        "Cannot derive bulk-insert batch size for dialect 'Pgsql'. Row width 65536 exceeds max parameters per command (65535)."
    )]
    public void It_should_fail_fast_when_row_width_exceeds_dialect_parameter_limit(
        SqlDialect dialect,
        int parametersPerRow,
        string expectedMessage
    )
    {
        var act = () => PlanWriteBatchingConventions.DeriveBulkInsertBatchingInfo(dialect, parametersPerRow);

        act.Should().Throw<InvalidOperationException>().WithMessage(expectedMessage);
    }

    [Test]
    public void It_should_fail_fast_when_dialect_is_unsupported()
    {
        const SqlDialect unsupportedDialect = (SqlDialect)999;

        var act = () => PlanWriteBatchingConventions.DeriveBulkInsertBatchingInfo(unsupportedDialect, 1);

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithMessage("Unsupported SQL dialect. (Parameter 'dialect')*")
            .WithParameterName("dialect");
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
        batchingInfo.MaxRowsPerBatch.Should().Be(524);
        batchingInfo.MaxParametersPerCommand.Should().Be(2098);
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
