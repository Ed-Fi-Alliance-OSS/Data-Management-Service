// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data.Common;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal sealed record DatabaseFingerprintColumnNames(
    string EffectiveSchemaSingletonId,
    string ApiSchemaFormatVersion,
    string EffectiveSchemaHash,
    string ResourceKeyCount,
    string ResourceKeySeedHash
);

internal sealed record DatabaseFingerprintReaderQuery(
    string TableDisplayName,
    string ExistsCommandText,
    string ReadCommandText,
    DatabaseFingerprintColumnNames ColumnNames
);

internal static class DatabaseFingerprintReaderSupport
{
    private const short ExpectedSingletonId = 1;
    private const int EffectiveSchemaHashLength = 64;
    private const int ResourceKeySeedHashLength = 32;

    public static async Task<DatabaseFingerprint?> ReadFingerprintAsync(
        Func<DbConnection> connectionFactory,
        DatabaseFingerprintReaderQuery query,
        ILogger logger
    )
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(logger);

        await using var connection = connectionFactory();
        await connection.OpenAsync();

        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = query.ExistsCommandText;

        if (await existsCommand.ExecuteScalarAsync() is null)
        {
            logger.LogDebug("{TableDisplayName} table does not exist", query.TableDisplayName);
            return null;
        }

        await using var readCommand = connection.CreateCommand();
        readCommand.CommandText = query.ReadCommandText;

        await using var reader = await readCommand.ExecuteReaderAsync();
        var fingerprint = await ReadValidatedFingerprintAsync(
            reader,
            query.ColumnNames,
            query.TableDisplayName
        );

        if (fingerprint is null)
        {
            logger.LogDebug(
                "{TableDisplayName} table exists but has no singleton row",
                query.TableDisplayName
            );
        }

        return fingerprint;
    }

    internal static async Task<DatabaseFingerprint?> ReadValidatedFingerprintAsync(
        DbDataReader reader,
        DatabaseFingerprintColumnNames columns,
        string tableDisplayName
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableDisplayName);

        if (!await reader.ReadAsync())
        {
            return null;
        }

        var fingerprint = ValidateFingerprint(
            tableDisplayName,
            ReadRequiredInt16(reader, columns.EffectiveSchemaSingletonId, tableDisplayName),
            ReadRequiredString(reader, columns.ApiSchemaFormatVersion, tableDisplayName),
            ReadRequiredString(reader, columns.EffectiveSchemaHash, tableDisplayName),
            ReadRequiredInt16(reader, columns.ResourceKeyCount, tableDisplayName),
            ReadRequiredBytes(reader, columns.ResourceKeySeedHash, tableDisplayName)
        );

        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                $"{tableDisplayName} must contain exactly one singleton row, but multiple rows were found."
            );
        }

        return fingerprint;
    }

    private static DatabaseFingerprint ValidateFingerprint(
        string tableDisplayName,
        short effectiveSchemaSingletonId,
        string apiSchemaFormatVersion,
        string effectiveSchemaHash,
        short resourceKeyCount,
        byte[] resourceKeySeedHash
    )
    {
        if (effectiveSchemaSingletonId != ExpectedSingletonId)
        {
            throw new InvalidOperationException(
                $"{tableDisplayName} must contain a singleton row with EffectiveSchemaSingletonId = 1, but found {effectiveSchemaSingletonId}."
            );
        }

        if (string.IsNullOrWhiteSpace(apiSchemaFormatVersion))
        {
            throw new InvalidOperationException(
                $"{tableDisplayName}.ApiSchemaFormatVersion must not be empty."
            );
        }

        if (!IsValidLowercaseHex(effectiveSchemaHash, EffectiveSchemaHashLength))
        {
            throw new InvalidOperationException(
                $"{tableDisplayName}.EffectiveSchemaHash must be 64 lowercase hex characters."
            );
        }

        if (resourceKeyCount < 0)
        {
            throw new InvalidOperationException(
                $"{tableDisplayName}.ResourceKeyCount must be non-negative, but found {resourceKeyCount}."
            );
        }

        if (resourceKeySeedHash.Length != ResourceKeySeedHashLength)
        {
            throw new InvalidOperationException(
                $"{tableDisplayName}.ResourceKeySeedHash must be exactly 32 bytes, but found {resourceKeySeedHash.Length}."
            );
        }

        return new DatabaseFingerprint(
            apiSchemaFormatVersion,
            effectiveSchemaHash,
            resourceKeyCount,
            resourceKeySeedHash.ToImmutableArray()
        );
    }

    private static string ReadRequiredString(DbDataReader reader, string columnName, string tableDisplayName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidOperationException($"{tableDisplayName}.{columnName} must not be null.");
        }

        return reader.GetString(ordinal);
    }

    private static short ReadRequiredInt16(DbDataReader reader, string columnName, string tableDisplayName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidOperationException($"{tableDisplayName}.{columnName} must not be null.");
        }

        return reader.GetInt16(ordinal);
    }

    private static byte[] ReadRequiredBytes(DbDataReader reader, string columnName, string tableDisplayName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidOperationException($"{tableDisplayName}.{columnName} must not be null.");
        }

        if (reader.GetValue(ordinal) is not byte[] value)
        {
            throw new InvalidOperationException($"{tableDisplayName}.{columnName} must be a byte array.");
        }

        return value;
    }

    private static bool IsValidLowercaseHex(string value, int expectedLength)
    {
        if (value.Length != expectedLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c) && (c < 'a' || c > 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
