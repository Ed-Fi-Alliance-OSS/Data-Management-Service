// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.Token;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

public class EducationOrganizationRepository(
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<EducationOrganizationRepository> logger
) : IEducationOrganizationRepository
{
    public async Task<IReadOnlyList<TokenInfoEducationOrganization>> GetEducationOrganizationsAsync(
        IEnumerable<long> educationOrganizationIds
    )
    {
        var edOrgIdsList = educationOrganizationIds.ToList();

        if (!edOrgIdsList.Any())
        {
            return Array.Empty<TokenInfoEducationOrganization>();
        }

        await using var connection = new NpgsqlConnection(databaseOptions.Value.DatabaseConnection);
        await connection.OpenAsync();

        try
        {
            const string Sql = """
                -- Get enriched education organization data with parent relationships
                WITH EdOrgBase AS (
                    SELECT
                        eoh.EducationOrganizationId,
                        d.ResourceName as OrganizationType,
                        d.EdfiDoc->>'nameOfInstitution' as NameOfInstitution,
                        eoh.ParentId
                    FROM dms.EducationOrganizationHierarchy eoh
                    INNER JOIN dms.Document d
                        ON eoh.DocumentId = d.Id
                        AND eoh.DocumentPartitionKey = d.DocumentPartitionKey
                    WHERE eoh.EducationOrganizationId = ANY(@EducationOrganizationIds)
                ),
                ParentData AS (
                    SELECT
                        child.EducationOrganizationId as ChildEdOrgId,
                        parent_eoh.EducationOrganizationId as ParentEdOrgId,
                        parent_doc.ResourceName as ParentType
                    FROM EdOrgBase child
                    INNER JOIN dms.EducationOrganizationHierarchy parent_eoh
                        ON child.ParentId = parent_eoh.Id
                    LEFT JOIN dms.Document parent_doc
                        ON parent_eoh.DocumentId = parent_doc.Id
                        AND parent_eoh.DocumentPartitionKey = parent_doc.DocumentPartitionKey
                )
                SELECT
                    eb.EducationOrganizationId,
                    eb.NameOfInstitution,
                    'edfi.' || eb.OrganizationType as Type,
                    CASE
                        WHEN eb.OrganizationType = 'School' THEN
                            (SELECT ParentEdOrgId FROM ParentData WHERE ChildEdOrgId = eb.EducationOrganizationId AND ParentType = 'LocalEducationAgency')
                        ELSE NULL
                    END as LocalEducationAgencyId,
                    CASE
                        WHEN eb.OrganizationType = 'LocalEducationAgency' THEN
                            (SELECT ParentEdOrgId FROM ParentData WHERE ChildEdOrgId = eb.EducationOrganizationId AND ParentType = 'EducationServiceCenter')
                        ELSE NULL
                    END as EducationServiceCenterId
                FROM EdOrgBase eb
                ORDER BY eb.EducationOrganizationId;
                """;

            var results = await connection.QueryAsync<TokenInfoEducationOrganization>(
                Sql,
                new { EducationOrganizationIds = edOrgIdsList.ToArray() }
            );

            return results.ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving education organizations for token info");
            return null!;
        }
    }
}
