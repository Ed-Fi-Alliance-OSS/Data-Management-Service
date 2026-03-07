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

public class PostgresqlDatabaseFingerprintReader(ILogger<PostgresqlDatabaseFingerprintReader> logger)
    : IDatabaseFingerprintReader
{
    private static readonly DatabaseFingerprintReaderQuery _query =
        DatabaseFingerprintReaderSupport.GetEffectiveSchemaQuery(SqlDialect.Pgsql);

    public Task<DatabaseFingerprint?> ReadFingerprintAsync(string connectionString) =>
        DatabaseFingerprintReaderSupport.ReadFingerprintAsync(
            () => new NpgsqlConnection(connectionString),
            _query,
            logger
        );
}
