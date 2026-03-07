// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Old.Postgresql.Startup;

internal interface IPostgresqlRuntimeDatabaseMetadataReader
{
    Task<PostgresqlDatabaseFingerprintReadResult> ReadFingerprintAsync(
        string connectionString,
        CancellationToken cancellationToken
    );

    Task<PostgresqlResourceKeyReadResult> ReadResourceKeysAsync(
        string connectionString,
        CancellationToken cancellationToken
    );
}

internal sealed class PostgresqlRuntimeDatabaseMetadataReader(NpgsqlDataSourceCache dataSourceCache)
    : IPostgresqlRuntimeDatabaseMetadataReader
{
    private const string EffectiveSchemaExistsSql =
        "SELECT 1 FROM information_schema.tables WHERE table_schema = 'dms' AND table_name = 'EffectiveSchema'";

    private const string EffectiveSchemaSelectSql = """
        SELECT "ApiSchemaFormatVersion", "EffectiveSchemaHash", "ResourceKeyCount", "ResourceKeySeedHash"
        FROM dms."EffectiveSchema"
        ORDER BY "EffectiveSchemaSingletonId"
        """;

    private const string ResourceKeyExistsSql =
        "SELECT 1 FROM information_schema.tables WHERE table_schema = 'dms' AND table_name = 'ResourceKey'";

    private const string ResourceKeySelectSql = """
        SELECT "ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion"
        FROM dms."ResourceKey"
        ORDER BY "ResourceKeyId"
        """;

    public async Task<PostgresqlDatabaseFingerprintReadResult> ReadFingerprintAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = await dataSourceCache
            .GetOrCreate(connectionString)
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = EffectiveSchemaExistsSql;

        if (await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return new PostgresqlDatabaseFingerprintReadResult.MissingEffectiveSchemaTable();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = EffectiveSchemaSelectSql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new PostgresqlDatabaseFingerprintReadResult.MissingEffectiveSchemaRow();
        }

        var fingerprint = new PostgresqlDatabaseFingerprint(
            ApiSchemaFormatVersion: reader.GetString(0),
            EffectiveSchemaHash: reader.GetString(1),
            ResourceKeyCount: reader.GetInt16(2),
            ResourceKeySeedHash: await reader
                .GetFieldValueAsync<byte[]>(3, cancellationToken)
                .ConfigureAwait(false)
        );

        var rowCount = 1;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rowCount++;
        }

        return rowCount == 1
            ? new PostgresqlDatabaseFingerprintReadResult.Success(fingerprint)
            : new PostgresqlDatabaseFingerprintReadResult.InvalidEffectiveSchemaSingleton(rowCount);
    }

    public async Task<PostgresqlResourceKeyReadResult> ReadResourceKeysAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = await dataSourceCache
            .GetOrCreate(connectionString)
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = ResourceKeyExistsSql;

        if (await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return new PostgresqlResourceKeyReadResult.MissingResourceKeyTable();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = ResourceKeySelectSql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<PostgresqlResourceKeyRow>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(
                new PostgresqlResourceKeyRow(
                    reader.GetInt16(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)
                )
            );
        }

        return new PostgresqlResourceKeyReadResult.Success(rows);
    }
}

internal sealed record PostgresqlDatabaseFingerprint(
    string ApiSchemaFormatVersion,
    string EffectiveSchemaHash,
    short ResourceKeyCount,
    byte[] ResourceKeySeedHash
);

internal abstract record PostgresqlDatabaseFingerprintReadResult
{
    public sealed record Success(PostgresqlDatabaseFingerprint Fingerprint)
        : PostgresqlDatabaseFingerprintReadResult;

    public sealed record MissingEffectiveSchemaTable : PostgresqlDatabaseFingerprintReadResult;

    public sealed record MissingEffectiveSchemaRow : PostgresqlDatabaseFingerprintReadResult;

    public sealed record InvalidEffectiveSchemaSingleton(int RowCount)
        : PostgresqlDatabaseFingerprintReadResult;
}

internal sealed record PostgresqlResourceKeyRow(
    short ResourceKeyId,
    string ProjectName,
    string ResourceName,
    string ResourceVersion
);

internal abstract record PostgresqlResourceKeyReadResult
{
    public sealed record Success(IReadOnlyList<PostgresqlResourceKeyRow> Rows)
        : PostgresqlResourceKeyReadResult;

    public sealed record MissingResourceKeyTable : PostgresqlResourceKeyReadResult;
}
