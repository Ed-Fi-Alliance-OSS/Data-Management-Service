// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category(MssqlCiShards.Shard3)]
public class Given_Mssql_RelationalQuery_Parameter_Binding
{
    private string _databaseName = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("MSSQL connection string not configured.");
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'dms') EXEC('CREATE SCHEMA [dms]');

            IF NOT EXISTS (
                SELECT *
                FROM sys.table_types t
                INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = 'dms' AND t.name = 'BigIntTable'
            )
            EXEC('CREATE TYPE [dms].[BigIntTable] AS TABLE ([Id] bigint NOT NULL)');
            """;

        await command.ExecuteNonQueryAsync();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null && MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public async Task It_executes_a_structured_claim_education_organization_id_parameter()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT input.[Id]
            FROM @ClaimEducationOrganizationIds input
            ORDER BY input.[Id]
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;

        HydrationBatchBuilder.AddParameters(command, CreateKeyset());

        var parameter = command.Parameters["@ClaimEducationOrganizationIds"];
        parameter.SqlDbType.Should().Be(SqlDbType.Structured);
        parameter.TypeName.Should().Be("dms.BigIntTable");
        parameter.Value.Should().BeOfType<DataTable>();

        var valueTable = (DataTable)parameter.Value;
        valueTable.Columns.Should().ContainSingle();
        valueTable.Columns[0].ColumnName.Should().Be("Id");
        valueTable.Columns[0].DataType.Should().Be(typeof(long));

        List<long> actualIds = [];
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            actualIds.Add(reader.GetInt64(0));
        }

        actualIds.Should().Equal(20L);
    }

    private static PageKeysetSpec.Query CreateKeyset()
    {
        return new PageKeysetSpec.Query(
            new PageDocumentIdSqlPlan(
                PageDocumentIdSql: "SELECT input.[Id] FROM @ClaimEducationOrganizationIds input",
                TotalCountSql: "SELECT COUNT(1) FROM @ClaimEducationOrganizationIds input",
                PageParametersInOrder:
                [
                    new QuerySqlParameter(
                        QuerySqlParameterRole.Filter,
                        "ClaimEducationOrganizationIds",
                        QuerySqlParameterBinding.CreateMssqlStructured("dms.BigIntTable", "Id")
                    ),
                    new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                    new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
                ],
                TotalCountParametersInOrder:
                [
                    new QuerySqlParameter(
                        QuerySqlParameterRole.Filter,
                        "ClaimEducationOrganizationIds",
                        QuerySqlParameterBinding.CreateMssqlStructured("dms.BigIntTable", "Id")
                    ),
                ]
            ),
            new Dictionary<string, object?>
            {
                ["ClaimEducationOrganizationIds"] = new long[] { 10L, 20L, 30L },
                ["offset"] = 1L,
                ["limit"] = 1L,
            }
        );
    }
}
