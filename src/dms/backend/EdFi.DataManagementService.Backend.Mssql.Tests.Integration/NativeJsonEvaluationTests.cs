// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Evaluation spike for the SQL Server native <c>json</c> data type, per the "Evaluation Spike (In
/// Scope Under Defer)" section of
/// reference/design/backend-redesign/epics/17-mssql-gap-closure/03-sql-server-2025-and-native-json.md.
///
/// This fixture creates its own scratch <c>DocumentCache</c>-shaped table with literal SQL, entirely
/// separate from the generated DDL pipeline: it does not call <c>MssqlDialect</c> or
/// <c>CoreDdlEmitter</c>, and does not touch goldens or manifests. The native <c>json</c> type is
/// still a preview feature for boxed SQL Server 2025, so tests assert observed provider and engine
/// behavior rather than only the documented surface; deviations are called out inline as recorded
/// findings for the follow-up adoption ticket.
/// </summary>
[TestFixture]
[Category(MssqlCiShards.Shard1)]
public class NativeJsonEvaluationTests
{
    private const int MinimumNativeJsonProductMajorVersion = 17;
    private const string ScratchTableName = "dbo.DocumentCacheEvaluation";

    private const string ObjectDocument = "{\"a\":1}";
    private const string ArrayDocument = "[1,2]";
    private const string ScalarDocument = "42";

    private string? _databaseName;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.json (or appsettings.Test.json)"
            );
        }

        var rawProductMajorVersion = await ReadProductMajorVersionAsync();

        if (
            !int.TryParse(rawProductMajorVersion, out var productMajorVersion)
            || productMajorVersion < MinimumNativeJsonProductMajorVersion
        )
        {
            Assert.Ignore(
                "native json evaluation requires SQL Server 2025+ "
                    + $"(SERVERPROPERTY('ProductMajorVersion') >= {MinimumNativeJsonProductMajorVersion}); "
                    + $"observed ProductMajorVersion='{rawProductMajorVersion ?? "<null>"}'."
            );
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            CREATE TABLE {{ScratchTableName}} (
                Id bigint IDENTITY(1,1) PRIMARY KEY,
                DocumentJson json NOT NULL
                    CONSTRAINT CK_DocumentCacheEvaluation_DocumentJson_Object CHECK (ISJSON(DocumentJson, OBJECT) = 1)
            );
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
    public async Task It_creates_the_scratch_table_with_a_native_json_DocumentJson_column()
    {
        var typeName = await ReadDocumentJsonCatalogTypeNameAsync();

        typeName
            .Should()
            .Be(
                "json",
                "sys.columns joined to sys.types resolves DocumentJson to the native json system type"
            );
    }

    [Test]
    public async Task It_accepts_a_json_object_via_the_object_only_constraint()
    {
        var id = await InsertDocumentAsync(ObjectDocument);

        id.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task It_rejects_a_json_array_via_the_object_only_constraint()
    {
        var act = () => InsertDocumentAsync(ArrayDocument);

        var exception = (await act.Should().ThrowAsync<SqlException>()).Which;
        exception.Message.Should().Contain("CK_DocumentCacheEvaluation_DocumentJson_Object");
    }

    [Test]
    public async Task It_rejects_a_scalar_value_before_the_object_only_constraint_is_even_evaluated()
    {
        // Recorded finding: "42" is valid JSON text per RFC 8259, but the native json type itself
        // requires an object or array on assignment and raises this error during that conversion,
        // before CK_DocumentCacheEvaluation_DocumentJson_Object is evaluated at all. A CHECK
        // constraint scoped to objects is therefore redundant for excluding bare scalars; it remains
        // necessary to also exclude arrays, which the native type accepts on its own.
        var act = () => InsertDocumentAsync(ScalarDocument);

        var exception = (await act.Should().ThrowAsync<SqlException>()).Which;
        exception.Message.Should().Contain("JSON text is not properly formatted");
    }

    [Test]
    public async Task It_round_trips_an_object_document_using_an_explicit_SqlDbType_Json_parameter()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            $"INSERT INTO {ScratchTableName} (DocumentJson) OUTPUT INSERTED.Id VALUES (@document);";
        var parameter = new SqlParameter("@document", SqlDbType.Json) { Value = ObjectDocument };
        insertCommand.Parameters.Add(parameter);

        var id = (long)(await insertCommand.ExecuteScalarAsync())!;

        var roundTripped = await SelectDocumentByIdAsync(id);
        roundTripped.Should().Be(ObjectDocument);
    }

    [Test]
    public async Task It_round_trips_an_object_document_using_an_inferred_clr_string_parameter()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            $"INSERT INTO {ScratchTableName} (DocumentJson) OUTPUT INSERTED.Id VALUES (@document);";
        insertCommand.Parameters.AddWithValue("@document", ObjectDocument);

        var id = (long)(await insertCommand.ExecuteScalarAsync())!;

        var roundTripped = await SelectDocumentByIdAsync(id);
        roundTripped.Should().Be(ObjectDocument);
    }

    [Test]
    public async Task It_queries_the_native_json_column_with_OPENJSON_and_JSON_VALUE()
    {
        const string document = "{\"studentUniqueId\":\"12345\",\"firstName\":\"Grace\"}";
        var id = await InsertDocumentAsync(document);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var jsonValueCommand = connection.CreateCommand();
        jsonValueCommand.CommandText =
            $"SELECT JSON_VALUE(DocumentJson, '$.firstName') FROM {ScratchTableName} WHERE Id = @id;";
        jsonValueCommand.Parameters.AddWithValue("@id", id);
        var firstName = (string)(await jsonValueCommand.ExecuteScalarAsync())!;

        firstName.Should().Be("Grace");

        await using var openJsonCommand = connection.CreateCommand();
        openJsonCommand.CommandText = $$"""
            SELECT extracted.studentUniqueId, extracted.firstName
            FROM {{ScratchTableName}}
            CROSS APPLY OPENJSON(DocumentJson) WITH (
                studentUniqueId nvarchar(50) '$.studentUniqueId',
                firstName nvarchar(50) '$.firstName'
            ) AS extracted
            WHERE Id = @id;
            """;
        openJsonCommand.Parameters.AddWithValue("@id", id);
        await using var reader = await openJsonCommand.ExecuteReaderAsync();
        await reader.ReadAsync();

        reader.GetString(0).Should().Be("12345");
        reader.GetString(1).Should().Be("Grace");
    }

    [Test]
    public async Task It_bulk_copies_object_documents_into_the_native_json_column()
    {
        DataTable table = new();
        table.Columns.Add("DocumentJson", typeof(string));
        table.Rows.Add("{\"bulk\":1}");
        table.Rows.Add("{\"bulk\":2}");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Recorded finding: SqlBulkCopy succeeds writing into a native json column when the source
        // DataTable column is typed as CLR string. A failure here on a later engine or provider
        // build is a behavior change to capture in the adoption design.
        using SqlBulkCopy bulkCopy = new(connection) { DestinationTableName = ScratchTableName };
        bulkCopy.ColumnMappings.Add("DocumentJson", "DocumentJson");
        await bulkCopy.WriteToServerAsync(table);

        var loadedDocuments = await SelectAllDocumentsAsync();
        loadedDocuments.Should().Contain(["{\"bulk\":1}", "{\"bulk\":2}"]);
    }

    [Test]
    public async Task It_materializes_the_native_json_value_via_GetString_and_GetFieldValue_of_string()
    {
        var id = await InsertDocumentAsync(ObjectDocument);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DocumentJson FROM {ScratchTableName} WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        reader.GetString(0).Should().Be(ObjectDocument);
        (await reader.GetFieldValueAsync<string>(0)).Should().Be(ObjectDocument);
    }

    [Test]
    public async Task It_materializes_the_native_json_value_via_GetFieldValue_of_SqlJson()
    {
        var id = await InsertDocumentAsync(ObjectDocument);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DocumentJson FROM {ScratchTableName} WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        var sqlJson = await reader.GetFieldValueAsync<SqlJson>(0);

        sqlJson.IsNull.Should().BeFalse();
        sqlJson.Value.Should().Be(ObjectDocument);
    }

    [Test]
    public async Task It_reports_the_native_json_type_name_via_GetColumnSchema_and_GetSchemaTable()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DocumentJson FROM {ScratchTableName};";
        await using var reader = await command.ExecuteReaderAsync();

        // Recorded finding: Microsoft documents that TDS clients may see DocumentJson surfaced as
        // varchar(max)/nvarchar(max). Against SQL Server 2025 with Microsoft.Data.SqlClient 6.1.4,
        // both schema-inspection surfaces instead report the native json type name directly.
        DbColumn documentJsonColumn = (await reader.GetColumnSchemaAsync())[0];
        documentJsonColumn.DataTypeName.Should().Be("json");

        DataTable schemaTable = (await reader.GetSchemaTableAsync())!;
        schemaTable.Rows[0]["DataTypeName"].Should().Be("json");
    }

    private static async Task<string?> ReadProductMajorVersionAsync()
    {
        await using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS nvarchar(128));";

        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<string> ReadDocumentJsonCatalogTypeNameAsync()
    {
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.name
            FROM sys.columns c
            JOIN sys.types t ON c.user_type_id = t.user_type_id
            WHERE c.object_id = OBJECT_ID('dbo.DocumentCacheEvaluation') AND c.name = N'DocumentJson';
            """;

        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> InsertDocumentAsync(string documentJson)
    {
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO {ScratchTableName} (DocumentJson) OUTPUT INSERTED.Id VALUES (@document);";
        command.Parameters.AddWithValue("@document", documentJson);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> SelectDocumentByIdAsync(long id)
    {
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT DocumentJson FROM {ScratchTableName} WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id);

        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<List<string>> SelectAllDocumentsAsync()
    {
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT DocumentJson FROM {ScratchTableName};";
        await using var reader = await command.ExecuteReaderAsync();

        List<string> documents = [];

        while (await reader.ReadAsync())
        {
            documents.Add(reader.GetString(0));
        }

        return documents;
    }
}
