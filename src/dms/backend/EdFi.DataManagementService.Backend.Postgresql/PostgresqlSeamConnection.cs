// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// How the request-path PostgreSQL seams reach the acquisition boundary. Held in one place so the
/// relational command executor and the document hydrator cannot drift into two slightly different ways
/// of turning the request's data source into an open connection.
/// </summary>
/// <remarks>
/// The SQL Server sibling returns a leased connection, because there a lease is taken per open and has
/// to travel with the connection it belongs to. Here the lease belongs to the request scope - the
/// scoped data-source provider takes one lazily and the DI scope releases it - so a seam is handed a
/// plain connection and disposes only that.
/// </remarks>
internal static class PostgresqlSeamConnection
{
    /// <summary>
    /// Opens a connection against the target this request selected, with acquisition guarded.
    /// </summary>
    /// <remarks>
    /// The boundary spans the <see cref="NpgsqlDataSourceProvider.DataSource" /> read as well as the
    /// open: the first read of the scope takes the cache lease, which builds the data source and
    /// therefore parses the connection string, so a provider-invalid string fails there rather than at
    /// the open. Command execution stays outside; a failure running a statement is not an unreachable
    /// database.
    /// </remarks>
    public static Task<DbConnection> OpenGuardedAsync(
        NpgsqlDataSourceProvider dataSourceProvider,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(dataSourceProvider);

        return ConnectionAcquisition.GuardAsync<DbConnection>(
            async () =>
                await dataSourceProvider
                    .DataSource.OpenConnectionAsync(cancellationToken)
                    .ConfigureAwait(false),
            dataSourceProvider.TargetKind,
            PostgresqlConnectionAcquisitionFailure.IsExpected,
            PostgresqlConnectionAcquisitionFailure.Describe,
            logger,
            cancellationToken
        );
    }
}
