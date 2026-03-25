// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

public sealed partial class MssqlReferenceResolverTestDatabase : IAsyncDisposable
{
    private static readonly MssqlDialect _dialect = new(new MssqlDialectRules());
    private static readonly string _coreDdl = new CoreDdlEmitter(_dialect).Emit();
    private static readonly DbTableName _changeVersionSequence = new(
        new DbSchemaName("dms"),
        "ChangeVersionSequence"
    );
    private static readonly DbTableName _documentTable = new(new DbSchemaName("dms"), "Document");
    private bool _disposed;

    private MssqlReferenceResolverTestDatabase(
        string databaseName,
        string connectionString,
        ReferenceResolverIntegrationFixture fixture,
        MappingSet mappingSet
    )
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        Fixture = fixture;
        MappingSet = mappingSet;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public ReferenceResolverIntegrationFixture Fixture { get; }

    public MappingSet MappingSet { get; }

    public static async Task<MssqlReferenceResolverTestDatabase> CreateProvisionedAsync(
        ReferenceResolverIntegrationFixture? fixture = null
    )
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            throw new InvalidOperationException(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        fixture ??= ReferenceResolverIntegrationFixture.CreateDefault();

        var mappingSet = fixture.CreateMappingSet(SqlDialect.Mssql);
        var databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(databaseName);

        MssqlTestDatabaseHelper.CreateDatabase(databaseName);

        try
        {
            await ProvisionAsync(connectionString, mappingSet);

            return new(databaseName, connectionString, fixture, mappingSet);
        }
        catch
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(databaseName);
            throw;
        }
    }

    public async Task ResetAsync()
    {
        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();

        var tableNames = await ReadObjectNamesAsync(connection, "U");
        var identityTableNames = await ReadIdentityTableNamesAsync(connection);

        foreach (var tableName in tableNames)
        {
            await ExecuteNonQueryAsync(connection, $"DISABLE TRIGGER ALL ON {tableName};");
        }

        foreach (var tableName in tableNames)
        {
            await ExecuteNonQueryAsync(connection, $"ALTER TABLE {tableName} NOCHECK CONSTRAINT ALL;");
        }

        foreach (var tableName in tableNames)
        {
            await ExecuteNonQueryAsync(connection, $"DELETE FROM {tableName};");
        }

        foreach (var identityTableName in identityTableNames)
        {
            var escapedIdentityTableName = identityTableName.Replace("'", "''");
            await ExecuteNonQueryAsync(
                connection,
                $"DBCC CHECKIDENT ('{escapedIdentityTableName}', RESEED, 0);"
            );
        }

        await ExecuteNonQueryAsync(
            connection,
            $"""
            ALTER SEQUENCE {SqlIdentifierQuoter.QuoteTableName(
                SqlDialect.Mssql,
                _changeVersionSequence
            )} RESTART WITH 1;
            """
        );

        foreach (var tableName in tableNames)
        {
            await ExecuteNonQueryAsync(
                connection,
                $"ALTER TABLE {tableName} WITH CHECK CHECK CONSTRAINT ALL;"
            );
        }

        foreach (var tableName in tableNames)
        {
            await ExecuteNonQueryAsync(connection, $"ENABLE TRIGGER ALL ON {tableName};");
        }
    }

    public Task SeedAsync()
    {
        return SeedAsync(Fixture.SeedData);
    }

    public async Task SeedAsync(ReferenceResolverSeedData seedData)
    {
        ArgumentNullException.ThrowIfNull(seedData);

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            foreach (var batch in seedData.CreateTableBatches())
            {
                if (batch.Rows.Count == 0)
                {
                    continue;
                }

                await using var command = BuildInsertCommand(connection, batch);
                command.Transaction = transaction;
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            SqlConnection.ClearPool(connection);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        MssqlTestDatabaseHelper.DropDatabaseIfExists(DatabaseName);

        return ValueTask.CompletedTask;
    }

    private static async Task ProvisionAsync(string connectionString, MappingSet mappingSet)
    {
        var relationalDdl = new RelationalModelDdlEmitter(_dialect).Emit(mappingSet.Model);
        var additionalSchemaDdl = string.Join(
            Environment.NewLine,
            GetAdditionalSchemas(mappingSet.Model).Select(_dialect.CreateSchemaIfNotExists)
        );
        var setupSql = string.Join(Environment.NewLine, _coreDdl, additionalSchemaDdl, relationalDdl);

        await ExecuteBatchesAsync(connectionString, setupSql);
    }

    private static IReadOnlyList<DbSchemaName> GetAdditionalSchemas(DerivedRelationalModelSet model)
    {
        return
        [
            .. model
                .ConcreteResourcesInNameOrder.Where(resource =>
                    resource.StorageKind != ResourceStorageKind.SharedDescriptorTable
                )
                .SelectMany(resource =>
                    resource.RelationalModel.TablesInDependencyOrder.Select(table => table.Table.Schema)
                )
                .Concat(
                    model.AbstractIdentityTablesInNameOrder.Select(table => table.TableModel.Table.Schema)
                )
                .Concat(model.AbstractUnionViewsInNameOrder.Select(view => view.ViewName.Schema))
                .Where(schema => schema.Value != "dms")
                .Distinct(),
        ];
    }

    private static async Task ExecuteBatchesAsync(string connectionString, string sql)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            foreach (var batch in SplitOnGoBatchSeparator(sql))
            {
                await using SqlCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = batch;
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            SqlConnection.ClearPool(connection);
        }
    }

    private static SqlCommand BuildInsertCommand(
        SqlConnection connection,
        ReferenceResolverSeedTableBatch batch
    )
    {
        var command = connection.CreateCommand();
        var quotedTableName = SqlIdentifierQuoter.QuoteTableName(SqlDialect.Mssql, batch.Table);
        var quotedColumns = string.Join(
            ", ",
            batch.Columns.Select(column => SqlIdentifierQuoter.QuoteIdentifier(SqlDialect.Mssql, column))
        );
        List<string> valueRows = [];

        for (var rowIndex = 0; rowIndex < batch.Rows.Count; rowIndex++)
        {
            List<string> parameterNames = [];

            for (var columnIndex = 0; columnIndex < batch.Columns.Count; columnIndex++)
            {
                var parameterName = $"@p{rowIndex}_{columnIndex}";
                parameterNames.Add(parameterName);
                command.Parameters.AddWithValue(
                    parameterName,
                    batch.Rows[rowIndex][columnIndex] ?? DBNull.Value
                );
            }

            valueRows.Add($"({string.Join(", ", parameterNames)})");
        }

        var insertSql = $"""
            INSERT INTO {quotedTableName} ({quotedColumns})
            VALUES {string.Join(", ", valueRows)};
            """;

        command.CommandText =
            batch.Table == _documentTable
                ? $"""
                    SET IDENTITY_INSERT {quotedTableName} ON;
                    {insertSql}
                    SET IDENTITY_INSERT {quotedTableName} OFF;
                    """
                : insertSql;

        return command;
    }

    private static async Task<string[]> ReadObjectNamesAsync(SqlConnection connection, string objectType)
    {
        const string Sql = """
            SELECT QUOTENAME(s.name) + N'.' + QUOTENAME(o.name)
            FROM sys.objects o
            JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.type = @objectType
              AND s.name IN (N'dms', N'edfi', N'auth')
            ORDER BY s.name, o.name;
            """;

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.AddWithValue("@objectType", objectType);

        await using var reader = await command.ExecuteReaderAsync();
        List<string> objectNames = [];

        while (await reader.ReadAsync())
        {
            objectNames.Add(reader.GetString(0));
        }

        return [.. objectNames];
    }

    private static async Task<string[]> ReadIdentityTableNamesAsync(SqlConnection connection)
    {
        const string Sql = """
            SELECT DISTINCT QUOTENAME(s.name) + N'.' + QUOTENAME(t.name)
            FROM sys.identity_columns ic
            JOIN sys.tables t ON ic.object_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name IN (N'dms', N'edfi', N'auth')
            ORDER BY QUOTENAME(s.name) + N'.' + QUOTENAME(t.name);
            """;

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = Sql;

        await using var reader = await command.ExecuteReaderAsync();
        List<string> identityTableNames = [];

        while (await reader.ReadAsync())
        {
            identityTableNames.Add(reader.GetString(0));
        }

        return [.. identityTableNames];
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string sql)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static IEnumerable<string> SplitOnGoBatchSeparator(string sql) =>
        GoBatchSeparatorPattern().Split(sql).Select(batch => batch.Trim()).Where(batch => batch.Length > 0);

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoBatchSeparatorPattern();
}
