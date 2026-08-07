// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;

namespace EdFi.DataManagementService.Backend.Ddl;

public interface ICdcProviderDatabaseExecutor
{
    Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken);

    Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
        string sql,
        CancellationToken cancellationToken
    );
}

public sealed class DbConnectionCdcProviderDatabaseExecutor(
    DbConnection connection,
    DbTransaction? transaction = null
) : ICdcProviderDatabaseExecutor
{
    public async Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
        string sql,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<IReadOnlyDictionary<string, string?>> rows = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Dictionary<string, string?> row = [];
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                var value = await reader
                    .GetFieldValueAsync<object>(ordinal, cancellationToken)
                    .ConfigureAwait(false);
                row[reader.GetName(ordinal)] =
                    value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            rows.Add(row);
        }

        return rows;
    }

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (connection.State == ConnectionState.Open)
        {
            return;
        }

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
