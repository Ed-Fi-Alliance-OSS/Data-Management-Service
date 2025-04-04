// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql
{
    public class PostgresqlAuthorizationRepository(NpgsqlDataSource _dataSource) : IAuthorizationRepository
    {
        public async Task<long[]> GetAncestorEducationOrganizationIds(
            long[] educationOrganizationIds,
            TraceId traceId
        )
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new(
                $"""
                    WITH RECURSIVE ParentHierarchy(Id, EducationOrganizationId, ParentId) AS (
                    SELECT h.Id, h.EducationOrganizationId, h.ParentId
                    FROM dms.EducationOrganizationHierarchy h
                    WHERE h.EducationOrganizationId = ANY($1)

                    UNION ALL

                    SELECT parent.Id, parent.EducationOrganizationId, parent.ParentId
                    FROM dms.EducationOrganizationHierarchy parent
                    JOIN ParentHierarchy child ON parent.Id = child.ParentId
                    )
                    SELECT EducationOrganizationId
                    FROM ParentHierarchy
                    ORDER BY EducationOrganizationId
                    {SqlBuilder.SqlFor(LockOption.BlockUpdateDelete)};
                """,
                connection
            )
            {
                Parameters = { new() { Value = educationOrganizationIds } },
            };
            await command.PrepareAsync();

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

            List<long> edOrgIds = [];

            while (await reader.ReadAsync())
            {
                edOrgIds.Add(reader.GetInt64(reader.GetOrdinal("EducationOrganizationId")));
            }

            return edOrgIds.Distinct().ToArray();
        }
    }
}
