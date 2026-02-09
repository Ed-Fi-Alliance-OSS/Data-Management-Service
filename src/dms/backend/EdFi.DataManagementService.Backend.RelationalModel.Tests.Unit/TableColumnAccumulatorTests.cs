// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for invalid table models with missing key columns in the column inventory.
/// </summary>
[TestFixture]
public class Given_A_TableColumnAccumulator_When_A_Key_Column_Is_Missing_From_Table_Columns
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var table = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Student"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Student",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [],
            []
        );

        try
        {
            _ = new TableColumnAccumulator(table);
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should fail fast with table and column details.
    /// </summary>
    [Test]
    public void It_should_fail_fast_with_table_and_column_details()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("edfi.Student");
        _exception.Message.Should().Contain("DocumentId");
        _exception.Message.Should().Contain("DbTableModel.Columns");
    }
}
