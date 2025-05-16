// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationValidation;

public static class RelationshipsBasedAuthorizationHelper
{
    private const string NoPropertyFoundErrorMessage =
        "No '{PropertyName}' property could be found on the resource in order to perform authorization. Should a different authorization strategy be used?";

    public static bool HasSecurable(AuthorizationSecurableInfo[] securableInfos, string securableKey)
    {
        return securableInfos.AsEnumerable().Any(x => x.SecurableKey == securableKey);
    }

    private static bool IsAuthorized(AuthorizationFilter[] authorizationFilters, long[]? educationOrgIds)
    {
        var authorizedEdOrgIds = authorizationFilters
            .Select(f => long.TryParse(f.Value, out var id) ? (long?)id : null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value);

        return educationOrgIds != null
            && educationOrgIds.Length > 0
            && authorizedEdOrgIds.Any(id => educationOrgIds.Contains(id));
    }

    public static async Task<ResourceAuthorizationResult> ValidateEdOrgAuthorization(
        IAuthorizationRepository authorizationRepository,
        DocumentSecurityElements securityElements,
        AuthorizationFilter[] authorizationFilters,
        OperationType operationType
    )
    {
        List<EducationOrganizationId> requestSecurableEdOrgIds = securityElements
            .EducationOrganization.Select(e => e.Id)
            .ToList();

        if (requestSecurableEdOrgIds.Count == 0)
        {
            string error =
                "No 'EducationOrganizationIds' property could be found on the resource in order to perform authorization. Should a different authorization strategy be used?";
            return new ResourceAuthorizationResult.NotAuthorized([error]);
        }

        var requestEdOrgHierarchies = await Task.WhenAll(
            requestSecurableEdOrgIds.Select(async edOrgId =>
                new[] { edOrgId.Value }.Concat(
                    await authorizationRepository.GetAncestorEducationOrganizationIds([edOrgId.Value])
                )
            )
        );

        var filterEdOrgIds = authorizationFilters
            .Select(filter =>
                long.TryParse(filter.Value, out long filterEdOrgId) ? filterEdOrgId : (long?)null
            )
            .WhereNotNull()
            .ToHashSet();

        // Token must have access to *all* edOrg hierarchies
        var isAuthorized = Array.TrueForAll(
            requestEdOrgHierarchies,
            requestEdOrgIdHierarchy =>
                requestEdOrgIdHierarchy.Any(edOrgId => filterEdOrgIds.Contains(edOrgId))
        );

        if (!isAuthorized)
        {
            string edOrgIdsFromFilters = string.Join(", ", authorizationFilters.Select(x => $"'{x.Value}'"));
            string error =
                (operationType == OperationType.Get || operationType == OperationType.Delete)
                    ? $"Access to the resource item could not be authorized based on the caller's EducationOrganizationIds claims: {edOrgIdsFromFilters}."
                    : $"No relationships have been established between the caller's education organization id claims ({edOrgIdsFromFilters}) and properties of the resource item: 'EducationOrganizationId'.";
            return new ResourceAuthorizationResult.NotAuthorized([error]);
        }
        return new ResourceAuthorizationResult.Authorized();
    }

    public static async Task<ResourceAuthorizationResult> ValidateStudentAuthorization(
        IAuthorizationRepository authorizationRepository,
        DocumentSecurityElements securityElements,
        AuthorizationFilter[] authorizationFilters
    )
    {
        if (securityElements.Student.Length == 0)
        {
            string error = NoPropertyFoundErrorMessage.Replace("{PropertyName}", "Student");
            return new ResourceAuthorizationResult.NotAuthorized([error]);
        }

        var studentUniqueId = securityElements.Student[0].Value;

        var educationOrgIds = await authorizationRepository.GetEducationOrganizationsForStudent(
            studentUniqueId
        );

        bool isAuthorized = IsAuthorized(authorizationFilters, educationOrgIds);

        if (!isAuthorized)
        {
            string edOrgIdsFromFilters = string.Join(", ", authorizationFilters.Select(x => $"'{x.Value}'"));
            string error =
                $"No relationships have been established between the caller's education organization id claims ({edOrgIdsFromFilters}) and one or more of the following properties of the resource item: 'StudentUniqueId'.";
            return new ResourceAuthorizationResult.NotAuthorized([error]);
        }

        return new ResourceAuthorizationResult.Authorized();
    }

    public static async Task<ResourceAuthorizationResult> ValidateContactAuthorization(
        IAuthorizationRepository authorizationRepository,
        DocumentSecurityElements securityElements,
        AuthorizationFilter[] authorizationFilters
    )
    {
        if (securityElements.Contact.Length == 0)
        {
            string error = NoPropertyFoundErrorMessage.Replace("{PropertyName}", "Contact");
            return new ResourceAuthorizationResult.NotAuthorized([error]);
        }
        var contactUniqueId = securityElements.Contact[0].Value;

        var educationOrgIds = await authorizationRepository.GetEducationOrganizationsForContact(
            contactUniqueId
        );

        bool isAuthorized = IsAuthorized(authorizationFilters, educationOrgIds);

        if (!isAuthorized)
        {
            string edOrgIdsFromFilters = string.Join(", ", authorizationFilters.Select(x => $"'{x.Value}'"));
            string error =
                $"No relationships have been established between the caller's education organization id claims ({edOrgIdsFromFilters}) and one or more of the following properties of the resource item: 'ContactUniqueId'.";
            return new ResourceAuthorizationResult.NotAuthorized([error]);
        }

        return new ResourceAuthorizationResult.Authorized();
    }

    public static async Task<ResourceAuthorizationResult> ValidateStaffAuthorization(
        IAuthorizationRepository authorizationRepository,
        DocumentSecurityElements securityElements,
        AuthorizationFilter[] authorizationFilters
    )
    {
        if (securityElements.Staff.Length == 0)
        {
            string error = NoPropertyFoundErrorMessage.Replace("{PropertyName}", "Staff");
            return new ResourceAuthorizationResult.NotAuthorized([error]);
        }
        var staffUniqueId = securityElements.Staff[0].Value;
        var educationOrgIds = await authorizationRepository.GetEducationOrganizationsForStaff(staffUniqueId);
        bool isAuthorized = IsAuthorized(authorizationFilters, educationOrgIds);
        if (!isAuthorized)
        {
            string edOrgIdsFromFilters = string.Join(", ", authorizationFilters.Select(x => $"'{x.Value}'"));
            string error =
                $"No relationships have been established between the caller's education organization id claims ({edOrgIdsFromFilters}) and one or more of the following properties of the resource item: 'StaffUniqueId'.";
            return new ResourceAuthorizationResult.NotAuthorized([error]);
        }
        return new ResourceAuthorizationResult.Authorized();
    }
}
