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
    private const int DefaultCommandTimeoutSeconds = 300;
    private const int MaximumSeedInsertParameters = 2000;
    private static readonly MssqlDialect _dialect = new(new MssqlDialectRules());
    private static readonly string _coreDdl = new CoreDdlEmitter(_dialect).Emit();
    private static readonly string _resetSql = MssqlDatabaseResetSql.Build();
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
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = _resetSql;
        command.CommandTimeout = DefaultCommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync();
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
                foreach (var insertBatch in SplitInsertBatch(batch))
                {
                    await using var command = BuildInsertCommand(connection, insertBatch);
                    command.Transaction = transaction;
                    await command.ExecuteNonQueryAsync();
                }
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

    private static IEnumerable<ReferenceResolverSeedTableBatch> SplitInsertBatch(
        ReferenceResolverSeedTableBatch batch
    )
    {
        if (batch.Rows.Count == 0)
        {
            yield break;
        }

        var rowsPerBatch = Math.Max(1, MaximumSeedInsertParameters / batch.Columns.Count);

        for (var startIndex = 0; startIndex < batch.Rows.Count; startIndex += rowsPerBatch)
        {
            var rowCount = Math.Min(rowsPerBatch, batch.Rows.Count - startIndex);

            yield return new ReferenceResolverSeedTableBatch(
                batch.Table,
                batch.Columns,
                batch.Rows.Skip(startIndex).Take(rowCount).ToArray()
            );
        }
    }

    private static IEnumerable<string> SplitOnGoBatchSeparator(string sql) =>
        GoBatchSeparatorPattern().Split(sql).Select(batch => batch.Trim()).Where(batch => batch.Length > 0);

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoBatchSeparatorPattern();
}
