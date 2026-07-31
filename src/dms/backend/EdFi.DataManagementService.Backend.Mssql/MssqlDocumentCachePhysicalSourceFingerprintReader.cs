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

public sealed class MssqlDocumentCachePhysicalSourceFingerprintReader(
    ILogger<MssqlDocumentCachePhysicalSourceFingerprintReader> logger
) : IDocumentCachePhysicalSourceFingerprintReader
{
    private static readonly DocumentCachePhysicalSourceFingerprintReaderQuery _query =
        DocumentCachePhysicalSourceFingerprintReaderSupport.GetQuery(SqlDialect.Mssql);

    public RelationalProviderToken ProviderToken => RelationalProviderToken.SqlServer;

    public Task<DocumentCachePhysicalSourceFingerprintReadResult> ReadFingerprintAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCachePhysicalSourceFingerprintReaderSupport.ReadFingerprintAsync(
            () => new SqlConnection(connectionString),
            _query,
            logger,
            cancellationToken
        );
}
