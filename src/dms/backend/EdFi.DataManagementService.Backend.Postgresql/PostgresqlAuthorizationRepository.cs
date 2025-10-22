// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Interface;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Implements IAuthorizationRepository to retrieve the authorization related data from the database.
/// </summary>
public class PostgresqlAuthorizationRepository(NpgsqlDataSource _dataSource, ISqlAction sqlAction)
    : IAuthorizationRepository
{
    public async Task<long[]> GetAncestorEducationOrganizationIds(long[] educationOrganizationIds)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var organizationIds = await sqlAction.GetAncestorEducationOrganizationIds(
            educationOrganizationIds,
            connection,
            null
        );

        return organizationIds.Distinct().ToArray();
    }

    public async Task<long[]> GetEducationOrganizationsForContact(string contactUniqueId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();

        JsonElement? response = await sqlAction.GetContactStudentSchoolAuthorizationEducationOrganizationIds(
            contactUniqueId,
            connection,
            null
        );
        if (response == null)
        {
            return [];
        }
        string[] stringIds = JsonSerializer.Deserialize<string[]>(response.Value) ?? [];
        long[] edOrgIds = stringIds.Select(long.Parse).ToArray();
        return edOrgIds;
    }

    public async Task<long[]> GetEducationOrganizationsForStudent(string studentUniqueId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();

        JsonElement? response = await sqlAction.GetStudentSchoolAuthorizationEducationOrganizationIds(
            studentUniqueId,
            connection,
            null
        );
        if (response == null)
        {
            return [];
        }
        string[] stringIds = JsonSerializer.Deserialize<string[]>(response.Value) ?? [];
        long[] edOrgIds = stringIds.Select(long.Parse).ToArray();
        return edOrgIds;
    }

    public async Task<long[]> GetEducationOrganizationsForStudentResponsibility(string studentUniqueId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();

        JsonElement? response = await sqlAction.GetStudentEdOrgResponsibilityAuthorizationIds(
            studentUniqueId,
            connection,
            null
        );
        if (response == null)
        {
            return [];
        }
        string[] stringIds = JsonSerializer.Deserialize<string[]>(response.Value) ?? [];
        long[] edOrgIds = stringIds.Select(long.Parse).ToArray();
        return edOrgIds;
    }

    public async Task<long[]> GetEducationOrganizationsForStaff(string staffUniqueId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();

        JsonElement? response = await sqlAction.GetStaffEducationOrganizationAuthorizationEdOrgIds(
            staffUniqueId,
            connection,
            null
        );
        if (response == null)
        {
            return [];
        }
        string[] stringIds = JsonSerializer.Deserialize<string[]>(response.Value) ?? [];
        long[] edOrgIds = stringIds.Select(long.Parse).ToArray();
        return edOrgIds;
    }
}
