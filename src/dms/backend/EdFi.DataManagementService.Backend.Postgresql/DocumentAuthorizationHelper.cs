// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Backend;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Helper class to handle authorization for documents.
/// </summary>
public static class DocumentAuthorizationHelper
{
    public static async Task<(
        JsonElement? StudentEdOrgIds,
        JsonElement? ContactEdOrgIds
    )> GetAuthorizationEducationOrganizationIds(
        IUpdateRequest updateRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ISqlAction sqlAction
    )
    {
        return await GetAuthorizationEducationOrganizationIdsInternal(
            updateRequest,
            connection,
            transaction,
            sqlAction
        );
    }

    public static async Task<(
        JsonElement? StudentEdOrgIds,
        JsonElement? ContactEdOrgIds
    )> GetAuthorizationEducationOrganizationIds(
        IUpsertRequest upsertRequest,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ISqlAction sqlAction
    )
    {
        return await GetAuthorizationEducationOrganizationIdsInternal(
            upsertRequest,
            connection,
            transaction,
            sqlAction
        );
    }

    private static async Task<(
        JsonElement? StudentEdOrgIds,
        JsonElement? ContactEdOrgIds
    )> GetAuthorizationEducationOrganizationIdsInternal(
        object request,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ISqlAction sqlAction
    )
    {
        JsonElement? studentEdOrgIds = null;
        JsonElement? contactEdOrgIds = null;

        if (request is IUpdateRequest updateRequest)
        {
            if (
                updateRequest
                    .ResourceInfo.AuthorizationSecurableInfo.AsEnumerable()
                    .Any(x => x.SecurableKey == SecurityElementNameConstants.StudentUniqueId)
            )
            {
                studentEdOrgIds = await sqlAction.GetStudentSchoolAuthorizationEducationOrganizationIds(
                    updateRequest.DocumentSecurityElements.Student[0].Value,
                    connection,
                    transaction
                );
            }

            if (
                updateRequest
                    .ResourceInfo.AuthorizationSecurableInfo.AsEnumerable()
                    .Any(x => x.SecurableKey == SecurityElementNameConstants.ContactUniqueId)
            )
            {
                contactEdOrgIds =
                    await sqlAction.GetContactStudentSchoolAuthorizationEducationOrganizationIds(
                        updateRequest.DocumentSecurityElements.Contact[0].Value,
                        connection,
                        transaction
                    );
            }
        }
        else if (request is IUpsertRequest upsertRequest)
        {
            if (
                upsertRequest
                    .ResourceInfo.AuthorizationSecurableInfo.AsEnumerable()
                    .Any(x => x.SecurableKey == SecurityElementNameConstants.StudentUniqueId)
            )
            {
                studentEdOrgIds = await sqlAction.GetStudentSchoolAuthorizationEducationOrganizationIds(
                    upsertRequest.DocumentSecurityElements.Student[0].Value,
                    connection,
                    transaction
                );
            }

            if (
                upsertRequest
                    .ResourceInfo.AuthorizationSecurableInfo.AsEnumerable()
                    .Any(x => x.SecurableKey == SecurityElementNameConstants.ContactUniqueId)
            )
            {
                contactEdOrgIds =
                    await sqlAction.GetContactStudentSchoolAuthorizationEducationOrganizationIds(
                        upsertRequest.DocumentSecurityElements.Contact[0].Value,
                        connection,
                        transaction
                    );
            }
        }

        return (studentEdOrgIds, contactEdOrgIds);
    }

    public static async Task InsertSecurableDocument(
        IUpsertRequest upsertRequest,
        long newDocumentId,
        short documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ISqlAction sqlAction
    )
    {
        if (
            upsertRequest
                .ResourceInfo.AuthorizationSecurableInfo.AsEnumerable()
                .Any(x => x.SecurableKey == SecurityElementNameConstants.StudentUniqueId)
        )
        {
            await sqlAction.InsertStudentSecurableDocument(
                upsertRequest.DocumentSecurityElements.Student[0].Value,
                newDocumentId,
                documentPartitionKey,
                connection,
                transaction
            );
        }

        if (
            upsertRequest
                .ResourceInfo.AuthorizationSecurableInfo.AsEnumerable()
                .Any(x => x.SecurableKey == SecurityElementNameConstants.ContactUniqueId)
        )
        {
            await sqlAction.InsertContactSecurableDocument(
                upsertRequest.DocumentSecurityElements.Contact[0].Value,
                newDocumentId,
                documentPartitionKey,
                connection,
                transaction
            );
        }
    }

    public static async Task UpdateSecurableDocument(
        IUpsertRequest updateRequest,
        long documentId,
        short documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ISqlAction sqlAction
    )
    {
        if (
            updateRequest
                .ResourceInfo.AuthorizationSecurableInfo.AsEnumerable()
                .Any(x => x.SecurableKey == SecurityElementNameConstants.StudentUniqueId)
        )
        {
            await sqlAction.UpdateStudentSecurableDocument(
                updateRequest.DocumentSecurityElements.Student[0].Value,
                documentId,
                documentPartitionKey,
                connection,
                transaction
            );
        }
        if (
            updateRequest
                .ResourceInfo.AuthorizationSecurableInfo.AsEnumerable()
                .Any(x => x.SecurableKey == SecurityElementNameConstants.ContactUniqueId)
        )
        {
            await sqlAction.UpdateContactSecurableDocument(
                updateRequest.DocumentSecurityElements.Contact[0].Value,
                documentId,
                documentPartitionKey,
                connection,
                transaction
            );
        }
    }
}
