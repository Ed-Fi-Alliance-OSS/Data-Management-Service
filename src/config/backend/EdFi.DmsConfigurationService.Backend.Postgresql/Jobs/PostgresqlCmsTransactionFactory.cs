// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;

/// <summary>
/// Begins a CMS transaction on its own PostgreSQL connection (spec D-18). The transaction owns the
/// connection: disposing it without committing rolls back and releases the connection.
/// </summary>
public sealed class PostgresqlCmsTransactionFactory(IOptions<DatabaseOptions> databaseOptions)
    : ICmsTransactionFactory
{
    public async Task<ICmsTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = new(databaseOptions.Value.DatabaseConnection);
        try
        {
            await connection.OpenAsync(cancellationToken);
            NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            return new PostgresqlCmsTransaction(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class PostgresqlCmsTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
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
