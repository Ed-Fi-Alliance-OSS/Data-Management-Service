// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.Jobs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Jobs;

/// <summary>
/// Begins a CMS transaction on its own SQL Server connection (spec D-18). The transaction owns the
/// connection: disposing it without committing rolls back and releases the connection.
/// </summary>
public sealed class MssqlCmsTransactionFactory(IOptions<DatabaseOptions> databaseOptions)
    : ICmsTransactionFactory
{
    public async Task<ICmsTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        SqlConnection connection = new(databaseOptions.Value.DatabaseConnection);
        try
        {
            await connection.OpenAsync(cancellationToken);
            DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            return new MssqlCmsTransaction(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class MssqlCmsTransaction(SqlConnection connection, DbTransaction transaction)
        : ICmsTransaction
    {
        public DbTransaction Transaction => transaction;

        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken) =>
            transaction.RollbackAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            // Disposing an uncommitted transaction rolls it back.
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
