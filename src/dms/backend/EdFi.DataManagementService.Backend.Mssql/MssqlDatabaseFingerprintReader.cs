// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

public class MssqlDatabaseFingerprintReader : IDatabaseFingerprintReader
{
    private static readonly DatabaseFingerprintReaderQuery _query =
        DatabaseFingerprintReaderSupport.GetEffectiveSchemaQuery(SqlDialect.Mssql);

    private readonly IMssqlConnectionAcquisition _acquisition;
    private readonly ILogger<MssqlDatabaseFingerprintReader> _logger;

    public MssqlDatabaseFingerprintReader(
        IMssqlConnectionAcquisition acquisition,
        ILogger<MssqlDatabaseFingerprintReader> logger
    )
    {
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DatabaseFingerprint?> ReadFingerprintAsync(EffectiveDataStoreTarget target)
    {
        // The lease is held across the whole support call because the support owns the open, so the
        // pool identity stays claimed for as long as a connection from it can be in use.
        await using MssqlConnectionLease lease = await _acquisition.AcquireLeaseAsync(target);

        return await DatabaseFingerprintReaderSupport.ReadFingerprintAsync(
            lease.CreateConnection,
            _query,
            _logger,
            static exception => exception is SqlException { Number: 207 }
        );
    }
}
