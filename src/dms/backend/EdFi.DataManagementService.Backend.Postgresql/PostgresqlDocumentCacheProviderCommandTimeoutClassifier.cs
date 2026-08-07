// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlDocumentCacheProviderCommandTimeoutClassifier
    : IDocumentCacheProviderCommandTimeoutClassifier
{
    private const string QueryCanceledSqlState = "57014";

    public bool IsProviderCommandTimeout(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is TimeoutException
            || exception is NpgsqlException { SqlState: QueryCanceledSqlState }
            || exception is NpgsqlException && HasInnerTimeoutException(exception);
    }

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
