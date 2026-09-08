// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

public sealed class PostgresqlDocumentCacheInventoryValidator(
    ILogger<PostgresqlDocumentCacheInventoryValidator> logger
) : IDocumentCacheInventoryValidator
{
    private static readonly DocumentCacheInventoryValidatorQuery _query =
        DocumentCacheInventoryValidatorSupport.GetQuery(SqlDialect.Pgsql);

    public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

    public Task<DocumentCacheProviderInventoryValidationResult> ValidateInventoryAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    ) =>
        DocumentCacheInventoryValidatorSupport.ValidateInventoryAsync(
            () => new NpgsqlConnection(connectionString),
            _query,
            logger,
            cancellationToken
        );
}
