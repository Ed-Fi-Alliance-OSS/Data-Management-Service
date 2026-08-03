// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

public sealed class MssqlDocumentCacheLifecycleReader(ILogger<MssqlDocumentCacheLifecycleReader> logger)
    : IDocumentCacheLifecycleReader
{
    private static readonly DocumentCacheLifecycleReaderQuery _query =
        DocumentCacheLifecycleReaderSupport.GetQuery(SqlDialect.Mssql);

    public RelationalProviderToken ProviderToken => RelationalProviderToken.SqlServer;

    public Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheLifecycleReaderSupport.ReadLifecycleAsync(
            () => new SqlConnection(connectionString),
            _query,
            logger,
            cancellationToken
        );
}
