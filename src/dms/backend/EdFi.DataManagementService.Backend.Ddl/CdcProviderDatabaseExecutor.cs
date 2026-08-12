// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;

namespace EdFi.DataManagementService.Backend.Ddl;

public interface ICdcProviderDatabaseExecutor
{
    Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken);

    Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
        string sql,
        CancellationToken cancellationToken
    );
}

public interface ICdcProviderErrorIdentityMapper
{
    CdcProviderErrorIdentity? MapProviderErrorIdentity(Exception exception);
}

public sealed class DbConnectionCdcProviderDatabaseExecutor(
    DbConnection connection,
    DbTransaction? transaction = null,
    Func<DbException, CdcProviderErrorIdentity?>? providerErrorIdentityMapper = null
) : ICdcProviderDatabaseExecutor, ICdcProviderErrorIdentityMapper
{
    public DbConnectionCdcProviderDatabaseExecutor(DbConnection connection, DbTransaction? transaction)
        : this(connection, transaction, providerErrorIdentityMapper: null) { }

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

    public CdcProviderErrorIdentity? MapProviderErrorIdentity(Exception exception)
    {
        if (exception is not DbException dbException)
        {
            return null;
        }

        return providerErrorIdentityMapper?.Invoke(dbException) ?? DefaultProviderErrorIdentity(dbException);
    }

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (connection.State == ConnectionState.Open)
        {
            return;
        }

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CdcProviderErrorIdentity? DefaultProviderErrorIdentity(DbException exception)
    {
        if (
            TryReadPublicProviderNumberAndState(
                exception,
                out var providerErrorCode,
                out var providerErrorState
            )
        )
        {
            return new(providerErrorCode, providerErrorState);
        }

        return string.IsNullOrWhiteSpace(exception.SqlState) ? null : new(exception.SqlState, null);
    }

    private static bool TryReadPublicProviderNumberAndState(
        DbException exception,
        out string providerErrorCode,
        out string? providerErrorState
    )
    {
        providerErrorCode = "";
        providerErrorState = null;

        var exceptionType = exception.GetType();
        PropertyInfo? numberProperty = exceptionType.GetProperty(
            "Number",
            BindingFlags.Public | BindingFlags.Instance
        );
        if (numberProperty is null)
        {
            return false;
        }

        try
        {
            var number = numberProperty.GetValue(exception);
            if (number is null)
            {
                return false;
            }

            providerErrorCode = Convert.ToString(number, CultureInfo.InvariantCulture) ?? "";
            if (string.IsNullOrWhiteSpace(providerErrorCode))
            {
                return false;
            }

            PropertyInfo? stateProperty = exceptionType.GetProperty(
                "State",
                BindingFlags.Public | BindingFlags.Instance
            );
            if (stateProperty is not null)
            {
                var state = stateProperty.GetValue(exception);
                providerErrorState = state is null
                    ? null
                    : Convert.ToString(state, CultureInfo.InvariantCulture);
            }

            return true;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}

internal static class CdcProviderDatabaseExecutorExtensions
{
    internal static CdcProviderErrorIdentity? TryMapProviderErrorIdentity(
        this ICdcProviderDatabaseExecutor executor,
        Exception exception
    )
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(exception);

        return executor is ICdcProviderErrorIdentityMapper mapper
            ? mapper.MapProviderErrorIdentity(exception)
            : null;
    }
}
