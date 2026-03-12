// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

public sealed partial class MssqlFingerprintTestDatabase : IAsyncDisposable
{
    private static readonly string _coreDdl = new CoreDdlEmitter(
        new MssqlDialect(new MssqlDialectRules())
    ).Emit();

    private MssqlFingerprintTestDatabase(string databaseName, string connectionString)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public static async Task<MssqlFingerprintTestDatabase> CreateProvisionedAsync()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            throw new InvalidOperationException(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        var databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(databaseName);

        MssqlTestDatabaseHelper.CreateDatabase(databaseName);

        try
        {
            await ExecuteBatchesAsync(connectionString, _coreDdl);
            return new(databaseName, connectionString);
        }
        catch
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(databaseName);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        MssqlTestDatabaseHelper.DropDatabaseIfExists(DatabaseName);
        return ValueTask.CompletedTask;
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

    private static IEnumerable<string> SplitOnGoBatchSeparator(string sql) =>
        GoBatchSeparatorPattern().Split(sql).Select(batch => batch.Trim()).Where(batch => batch.Length > 0);

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoBatchSeparatorPattern();
}
