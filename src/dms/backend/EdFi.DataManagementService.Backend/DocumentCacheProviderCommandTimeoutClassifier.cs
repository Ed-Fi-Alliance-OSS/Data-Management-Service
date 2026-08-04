// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DataManagementService.Backend;

internal static class DocumentCacheProviderCommandTimeoutClassifier
{
    private const int SqlServerCommandTimeoutNumber = -2;
    private const string PostgresqlQueryCanceledSqlState = "57014";
    private const string MicrosoftSqlExceptionTypeName = "Microsoft.Data.SqlClient.SqlException";
    private const string SystemSqlExceptionTypeName = "System.Data.SqlClient.SqlException";
    private const string NpgsqlExceptionTypeName = "Npgsql.NpgsqlException";

    public static bool IsProviderCommandTimeout(Exception exception) =>
        exception is TimeoutException
        || IsSqlServerProviderCommandTimeout(exception)
        || IsPostgresqlProviderCommandTimeout(exception);

    private static bool IsSqlServerProviderCommandTimeout(Exception exception)
    {
        if (exception is not DbException)
        {
            return false;
        }

        string? typeName = exception.GetType().FullName;
        return typeName is MicrosoftSqlExceptionTypeName or SystemSqlExceptionTypeName
            && TryGetIntProperty(exception, "Number") == SqlServerCommandTimeoutNumber;
    }

    private static bool IsPostgresqlProviderCommandTimeout(Exception exception)
    {
        if (exception is not DbException || !IsTypeOrSubtype(exception.GetType(), NpgsqlExceptionTypeName))
        {
            return false;
        }

        return TryGetStringProperty(exception, "SqlState") == PostgresqlQueryCanceledSqlState
            || HasInnerTimeoutException(exception);
    }

    private static bool IsTypeOrSubtype(Type type, string fullName)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            if (current.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    private static int? TryGetIntProperty(Exception exception, string propertyName) =>
        exception.GetType().GetProperty(propertyName)?.GetValue(exception) is int value ? value : null;

    private static string? TryGetStringProperty(Exception exception, string propertyName) =>
        exception.GetType().GetProperty(propertyName)?.GetValue(exception) as string;

    private static bool HasInnerTimeoutException(Exception exception)
    {
        for (
            Exception? current = exception.InnerException;
            current is not null;
            current = current.InnerException
        )
        {
            if (current is TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}
