// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
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
        object request,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ISqlAction sqlAction
    )
    {
        // Extract security elements and authorization info based on request type
        var (securityElements, authInfo) = request switch
        {
            IUpdateRequest updateRequest => (
                updateRequest.DocumentSecurityElements,
                updateRequest.ResourceInfo.AuthorizationSecurableInfo
            ),
            IUpsertRequest upsertRequest => (
                upsertRequest.DocumentSecurityElements,
                upsertRequest.ResourceInfo.AuthorizationSecurableInfo
            ),
            _ => throw new ArgumentException(
                $"Unsupported request type: {request.GetType().Name}",
                nameof(request)
            ),
        };

        // Process student authorization if applicable
        JsonElement? studentEdOrgIds = null;
        if (
            HasSecurable(authInfo, SecurityElementNameConstants.StudentUniqueId)
            && securityElements.Student?.Length > 0
        )
        {
            studentEdOrgIds = await sqlAction.GetStudentSchoolAuthorizationEducationOrganizationIds(
                securityElements.Student[0].Value,
                connection,
                transaction
            );
        }

        // Process contact authorization if applicable
        JsonElement? contactEdOrgIds = null;
        if (
            HasSecurable(authInfo, SecurityElementNameConstants.ContactUniqueId)
            && securityElements.Contact?.Length > 0
        )
        {
            contactEdOrgIds = await sqlAction.GetContactStudentSchoolAuthorizationEducationOrganizationIds(
                securityElements.Contact[0].Value,
                connection,
                transaction
            );
        }

        return (studentEdOrgIds, contactEdOrgIds);
    }

    // Helper method to check if a securable key exists
    private static bool HasSecurable(IEnumerable<AuthorizationSecurableInfo> authInfo, string securableKey) =>
        authInfo.AsEnumerable().Any(x => x.SecurableKey == securableKey);

    public static async Task InsertSecurableDocument(
        IUpsertRequest upsertRequest,
        long newDocumentId,
        short documentPartitionKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ISqlAction sqlAction
    )
    {
        var securableInfo = upsertRequest.ResourceInfo.AuthorizationSecurableInfo.AsEnumerable();
        var securityElements = upsertRequest.DocumentSecurityElements;

        if (
            HasSecurable(securableInfo, SecurityElementNameConstants.StudentUniqueId)
            && securityElements.Student?.Length > 0
        )
        {
            await sqlAction.InsertStudentSecurableDocument(
                securityElements.Student[0].Value,
                newDocumentId,
                documentPartitionKey,
                connection,
                transaction
            );
        }

        if (
            HasSecurable(securableInfo, SecurityElementNameConstants.ContactUniqueId)
            && securityElements.Contact?.Length > 0
        )
        {
            await sqlAction.InsertContactSecurableDocument(
                securityElements.Contact[0].Value,
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
        var securableInfo = updateRequest.ResourceInfo.AuthorizationSecurableInfo.AsEnumerable();
        var securityElements = updateRequest.DocumentSecurityElements;

        if (
            HasSecurable(securableInfo, SecurityElementNameConstants.StudentUniqueId)
            && securityElements.Student?.Length > 0
        )
        {
            await sqlAction.UpdateStudentSecurableDocument(
                securityElements.Student[0].Value,
                documentId,
                documentPartitionKey,
                connection,
                transaction
            );
        }
        if (
            HasSecurable(securableInfo, SecurityElementNameConstants.ContactUniqueId)
            && securityElements.Contact?.Length > 0
        )
        {
            await sqlAction.UpdateContactSecurableDocument(
                securityElements.Contact[0].Value,
                documentId,
                documentPartitionKey,
                connection,
                transaction
            );
        }
    }
}
