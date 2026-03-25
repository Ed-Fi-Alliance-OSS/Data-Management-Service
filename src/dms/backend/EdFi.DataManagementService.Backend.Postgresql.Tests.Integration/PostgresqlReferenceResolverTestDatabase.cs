// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

public sealed class PostgresqlReferenceResolverTestDatabase : IAsyncDisposable
{
    private static readonly PgsqlDialect _dialect = new(new PgsqlDialectRules());
    private static readonly string _coreDdl = new CoreDdlEmitter(_dialect).Emit();
    private static readonly DbTableName _documentTable = new(new DbSchemaName("dms"), "Document");
    private static readonly string _resetTablesSql = """
        DO $$
        DECLARE
            truncate_sql text;
        BEGIN
            SELECT
                CASE
                    WHEN COUNT(*) = 0 THEN NULL
                    ELSE
                        'TRUNCATE TABLE '
                        || string_agg(
                            format('%I.%I', schemaname, tablename),
                            ', '
                            ORDER BY schemaname, tablename
                        )
                        || ' RESTART IDENTITY CASCADE;'
                END
            INTO truncate_sql
            FROM pg_tables
            WHERE schemaname IN ('dms', 'edfi', 'auth');

            IF truncate_sql IS NOT NULL THEN
                EXECUTE truncate_sql;
            END IF;
        END
        $$;
        """;

    private readonly NpgsqlDataSource _dataSource;
    private bool _disposed;

    private PostgresqlReferenceResolverTestDatabase(
        string databaseName,
        string connectionString,
        ReferenceResolverIntegrationFixture fixture,
        MappingSet mappingSet,
        NpgsqlDataSource dataSource
    )
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        Fixture = fixture;
        MappingSet = mappingSet;
        _dataSource = dataSource;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public ReferenceResolverIntegrationFixture Fixture { get; }

    public MappingSet MappingSet { get; }

    public static async Task<PostgresqlReferenceResolverTestDatabase> CreateProvisionedAsync(
        ReferenceResolverIntegrationFixture? fixture = null
    )
    {
        fixture ??= ReferenceResolverIntegrationFixture.CreateDefault();

        var mappingSet = fixture.CreateMappingSet(SqlDialect.Pgsql);
        var databaseName = PostgresqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = PostgresqlTestDatabaseHelper.BuildConnectionString(databaseName);

        PostgresqlTestDatabaseHelper.CreateDatabase(databaseName);

        try
        {
            await ProvisionAsync(connectionString, mappingSet);

            return new(
                databaseName,
                connectionString,
                fixture,
                mappingSet,
                NpgsqlDataSource.Create(connectionString)
            );
        }
        catch
        {
            PostgresqlTestDatabaseHelper.DropDatabaseIfExists(databaseName);
            throw;
        }
    }

    public async Task ResetAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = _resetTablesSql;
        await command.ExecuteNonQueryAsync();
    }

    public Task SeedAsync()
    {
        return SeedAsync(Fixture.SeedData);
    }

    public async Task SeedAsync(ReferenceResolverSeedData seedData)
    {
        ArgumentNullException.ThrowIfNull(seedData);

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

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
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _dataSource.DisposeAsync();
        PostgresqlTestDatabaseHelper.DropDatabaseIfExists(DatabaseName);
    }

    private static async Task ProvisionAsync(string connectionString, MappingSet mappingSet)
    {
        var relationalDdl = new RelationalModelDdlEmitter(_dialect).Emit(mappingSet.Model);
        var additionalSchemaDdl = string.Join(
            Environment.NewLine,
            GetAdditionalSchemas(mappingSet.Model).Select(_dialect.CreateSchemaIfNotExists)
        );

        var setupSql = string.Join(Environment.NewLine, _coreDdl, additionalSchemaDdl, relationalDdl);

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = setupSql;
        await command.ExecuteNonQueryAsync();
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

    private static NpgsqlCommand BuildInsertCommand(
        NpgsqlConnection connection,
        ReferenceResolverSeedTableBatch batch
    )
    {
        var command = connection.CreateCommand();
        var quotedColumns = string.Join(
            ", ",
            batch.Columns.Select(column => SqlIdentifierQuoter.QuoteIdentifier(SqlDialect.Pgsql, column))
        );
        List<string> valueRows = [];

        for (var rowIndex = 0; rowIndex < batch.Rows.Count; rowIndex++)
        {
            List<string> parameterNames = [];

            for (var columnIndex = 0; columnIndex < batch.Columns.Count; columnIndex++)
            {
                var parameterName = $"@p{rowIndex}_{columnIndex}";
                parameterNames.Add(parameterName);
                command.Parameters.Add(
                    new NpgsqlParameter(parameterName, batch.Rows[rowIndex][columnIndex] ?? DBNull.Value)
                );
            }

            valueRows.Add($"({string.Join(", ", parameterNames)})");
        }

        var identityOverrideClause =
            batch.Table == _documentTable ? " OVERRIDING SYSTEM VALUE" : string.Empty;

        command.CommandText = $"""
            INSERT INTO {SqlIdentifierQuoter.QuoteTableName(
                SqlDialect.Pgsql,
                batch.Table
            )} ({quotedColumns}){identityOverrideClause}
            VALUES {string.Join(", ", valueRows)};
            """;

        return command;
    }
}
