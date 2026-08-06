// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlDocumentCacheProviderCommandTimeoutClassifier
    : IDocumentCacheProviderCommandTimeoutClassifier
{
    private const int CommandTimeoutNumber = -2;

    public bool IsProviderCommandTimeout(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is TimeoutException or SqlException { Number: CommandTimeoutNumber };
    }
}
