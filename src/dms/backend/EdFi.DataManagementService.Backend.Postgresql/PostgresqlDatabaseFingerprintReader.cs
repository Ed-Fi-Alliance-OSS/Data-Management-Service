// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

// TODO(DMS-939): Add an integration test that reads from an actual provisioned PostgreSQL database
// to verify the reader's column names match the DDL-emitted schema.
public class PostgresqlDatabaseFingerprintReader(ILogger<PostgresqlDatabaseFingerprintReader> logger)
    : IDatabaseFingerprintReader
{
    private static readonly DatabaseFingerprintReaderQuery _query = new(
        "dms.EffectiveSchema",
        "SELECT 1 FROM information_schema.tables WHERE table_schema = 'dms' AND table_name = 'effectiveschema'",
        """
        SELECT effectiveschemasingletonid, apischemaformatversion, effectiveschemahash, resourcekeycount, resourcekeyseedhash
        FROM dms.effectiveschema
        ORDER BY effectiveschemasingletonid
        LIMIT 2
        """,
        new DatabaseFingerprintColumnNames(
            EffectiveSchemaSingletonId: "effectiveschemasingletonid",
            ApiSchemaFormatVersion: "apischemaformatversion",
            EffectiveSchemaHash: "effectiveschemahash",
            ResourceKeyCount: "resourcekeycount",
            ResourceKeySeedHash: "resourcekeyseedhash"
        )
    );

    public Task<DatabaseFingerprint?> ReadFingerprintAsync(string connectionString) =>
        DatabaseFingerprintReaderSupport.ReadFingerprintAsync(
            () => new NpgsqlConnection(connectionString),
            _query,
            logger
        );
}
