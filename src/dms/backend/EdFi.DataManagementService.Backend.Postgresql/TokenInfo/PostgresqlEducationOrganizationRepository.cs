// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.TokenInfo;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql.TokenInfo;

/// <summary>
/// PostgreSQL implementation for retrieving education organizations from the DMS tenant database
/// </summary>
public class PostgresqlEducationOrganizationRepository(
    NpgsqlDataSourceProvider dataSourceProvider,
    ISqlAction sqlAction,
    ILogger<PostgresqlEducationOrganizationRepository> logger
) : IEducationOrganizationRepository
{
    public async Task<IReadOnlyList<TokenInfoEducationOrganization>> GetEducationOrganizationsAsync(
        IEnumerable<long> ids
    )
    {
        var idList = ids.ToList();
        if (!idList.Any())
        {
            return Array.Empty<TokenInfoEducationOrganization>();
        }

        try
        {
            await using var connection = await dataSourceProvider.DataSource.OpenConnectionAsync();

            logger.LogDebug(
                "Fetching education organizations for IDs: {EducationOrganizationIds}",
                string.Join(",", idList)
            );

            var organizations = await sqlAction.GetEducationOrganizationsAsync(ids, connection);

            logger.LogDebug(
                "Retrieved {Count} education organizations from tenant database",
                organizations.Count
            );

            return organizations;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Error retrieving education organizations from tenant database for IDs: {EducationOrganizationIds}",
                string.Join(",", idList)
            );
            return [];
        }
    }
}
