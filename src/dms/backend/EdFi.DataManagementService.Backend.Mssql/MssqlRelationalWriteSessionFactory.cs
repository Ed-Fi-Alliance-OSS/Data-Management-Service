// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlRelationalWriteSessionFactory : IRelationalWriteSessionFactory
{
    private readonly Func<CancellationToken, Task<MssqlLeasedConnection>> _openConnectionAsync;
    private readonly IsolationLevel _isolationLevel;

    public MssqlRelationalWriteSessionFactory(
        IDataStoreSelection dataStoreSelection,
        IMssqlConnectionAcquisition acquisition,
        IOptions<DatabaseOptions> databaseOptions
    )
    {
        ArgumentNullException.ThrowIfNull(dataStoreSelection);
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentNullException.ThrowIfNull(databaseOptions);

        _isolationLevel = databaseOptions.Value.IsolationLevel;
        _openConnectionAsync = cancellationToken =>
            MssqlSeamConnection.OpenAsync(dataStoreSelection, acquisition, cancellationToken);
    }

    internal MssqlRelationalWriteSessionFactory(
        Func<CancellationToken, Task<DbConnection>> openConnectionAsync,
        IsolationLevel isolationLevel
    )
    {
        ArgumentNullException.ThrowIfNull(openConnectionAsync);

        _openConnectionAsync = async cancellationToken =>
            MssqlLeasedConnection.WithoutLease(
                await openConnectionAsync(cancellationToken).ConfigureAwait(false)
            );
        _isolationLevel = isolationLevel;
    }

    public async Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
    {
        var leased = await _openConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var transaction = await leased
                .Connection.BeginTransactionAsync(_isolationLevel, cancellationToken)
                .ConfigureAwait(false);

            // Ownership of the connection and of the claim it was drawn from both transfer to the
            // session, which disposes the connection first and releases the claim second.
            return new RelationalWriteSession(
                leased.Connection,
                transaction,
                MssqlTransactionStateProbe.Instance,
                ownedLease: leased.Lease
            );
        }
        catch
        {
            // Cleanup must not replace the transaction failure the caller needs to see.
            await MssqlLeasedConnection.DisposeWithoutMaskingAsync(leased).ConfigureAwait(false);
            throw;
        }
    }
}
