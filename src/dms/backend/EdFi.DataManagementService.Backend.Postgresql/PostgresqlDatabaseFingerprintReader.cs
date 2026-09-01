// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Reads the effective-schema fingerprint from whichever database the request selected.
/// </summary>
/// <remarks>
/// The connection comes from the leased data-source cache rather than from the target's connection
/// string directly. This is the first database touch of any request, so a raw connection here would
/// put the very first read of a snapshot or read replica outside the data source the ownership rules
/// govern - the one place a derivative's pool is built, retired, and disposed with its configuration.
/// </remarks>
public class PostgresqlDatabaseFingerprintReader(
    NpgsqlDataSourceCache dataSourceCache,
    ILogger<PostgresqlDatabaseFingerprintReader> logger
) : IDatabaseFingerprintReader
{
    private static readonly DatabaseFingerprintReaderQuery _query =
        DatabaseFingerprintReaderSupport.GetEffectiveSchemaQuery(SqlDialect.Pgsql);

    public async Task<DatabaseFingerprint?> ReadFingerprintAsync(EffectiveDataStoreTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // The lease is held across the whole support call because the support owns the open, so the
        // data source stays alive for as long as a connection from it can be in use.
        await using NpgsqlDataSourceLease lease = dataSourceCache.AcquireLease(target.ConnectionString);

        return await DatabaseFingerprintReaderSupport.ReadFingerprintAsync(
            lease.CreateConnection,
            _query,
            logger,
            static exception =>
                exception is PostgresException { SqlState: PostgresErrorCodes.UndefinedColumn }
        );
    }
}
