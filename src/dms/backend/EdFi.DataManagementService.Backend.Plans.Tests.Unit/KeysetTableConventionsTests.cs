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
public class Given_KeysetTableConventions
{
    [TestCase(SqlDialect.Pgsql, "page")]
    [TestCase(SqlDialect.Mssql, "#page")]
    public void It_should_return_expected_dialect_specific_keyset_table_names(
        SqlDialect dialect,
        string expectedTableName
    )
    {
        var keysetContract = KeysetTableConventions.GetKeysetTableContract(dialect);

        keysetContract.TableName.Schema.Value.Should().BeEmpty();
        keysetContract.TableName.Name.Should().Be(expectedTableName);
        keysetContract.DocumentIdColumnName.Should().Be(new DbColumnName("DocumentId"));
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_return_deterministic_keyset_table_contract_values_across_calls(SqlDialect dialect)
    {
        var firstContract = KeysetTableConventions.GetKeysetTableContract(dialect);
        var secondContract = KeysetTableConventions.GetKeysetTableContract(dialect);

        secondContract.Should().Be(firstContract);
    }
}
