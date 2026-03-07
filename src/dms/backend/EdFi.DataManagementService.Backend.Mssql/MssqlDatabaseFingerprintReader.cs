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

public class MssqlDatabaseFingerprintReader(ILogger<MssqlDatabaseFingerprintReader> logger)
    : IDatabaseFingerprintReader
{
    private static readonly DatabaseFingerprintReaderQuery _query =
        DatabaseFingerprintReaderSupport.GetEffectiveSchemaQuery(SqlDialect.Mssql);

    public Task<DatabaseFingerprint?> ReadFingerprintAsync(string connectionString) =>
        DatabaseFingerprintReaderSupport.ReadFingerprintAsync(
            () => new SqlConnection(connectionString),
            _query,
            logger
        );
}
