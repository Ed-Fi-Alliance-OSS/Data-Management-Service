// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Backend;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

public static class AuthorizationDataHelper
{
    public static async Task<(
        JsonElement? StudentEdOrgIds,
        JsonElement? ContactEdOrgIds
    )> GetAuthorizationEducationOrganizationIds(
        IUpsertRequest upsertRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ISqlAction _sqlAction
    )
    {
        JsonElement? studentSchoolAuthorizationEducationOrganizationIds = null;
        JsonElement? contactStudentSchoolAuthorizationEducationOrganizationIds = null;

        // Check for StudentUniqueId securable
        if (
            upsertRequest
                .ResourceInfo.AuthorizationSecurableInfo.AsEnumerable()
                .Any(x => x.SecurableKey == SecurityElementNameConstants.StudentUniqueId)
        )
        {
            studentSchoolAuthorizationEducationOrganizationIds =
                await _sqlAction.GetStudentSchoolAuthorizationEducationOrganizationIds(
                    upsertRequest.DocumentSecurityElements.Student[0].Value,
                    connection,
                    transaction
                );
        }

        // Check for ContactUniqueId securable
        if (
            upsertRequest
                .ResourceInfo.AuthorizationSecurableInfo.AsEnumerable()
                .Any(x => x.SecurableKey == SecurityElementNameConstants.ContactUniqueId)
        )
        {
            contactStudentSchoolAuthorizationEducationOrganizationIds =
                await _sqlAction.GetContactStudentSchoolAuthorizationEducationOrganizationIds(
                    upsertRequest.DocumentSecurityElements.Contact[0].Value,
                    connection,
                    transaction
                );
        }

        // Check for both StudentUniqueId and ContactUniqueId securables
        if (
            upsertRequest
                .ResourceInfo.AuthorizationSecurableInfo.AsEnumerable()
                .Any(x => x.SecurableKey == SecurityElementNameConstants.ContactUniqueId)
            && upsertRequest
                .ResourceInfo.AuthorizationSecurableInfo.AsEnumerable()
                .Any(x => x.SecurableKey == SecurityElementNameConstants.StudentUniqueId)
        )
        {
            contactStudentSchoolAuthorizationEducationOrganizationIds =
                await _sqlAction.GetContactStudentSchoolAuthorizationEdOrgIdsForStudentAndContactSecurable(
                    upsertRequest.DocumentSecurityElements.Contact[0].Value,
                    upsertRequest.DocumentSecurityElements.Student[0].Value,
                    connection,
                    transaction
                );
        }

        return (
            studentSchoolAuthorizationEducationOrganizationIds,
            contactStudentSchoolAuthorizationEducationOrganizationIds
        );
    }
}
