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
    public static bool HasSecurable(AuthorizationSecurableInfo[] securableInfos, string securableKey)
    {
        return securableInfos.AsEnumerable().Any(x => x.SecurableKey == securableKey);
    }

    public static string BuildErrorMessage(
        AuthorizationFilter[] authorizationFilters,
        IEnumerable<string> missingProperties,
        IEnumerable<string> notAuthorizedProperties
    )
    {
        if (missingProperties != null && missingProperties.Any())
        {
            string propertyOrProperties = missingProperties.Count() > 1 ? "properties" : "property";
            return $"No {string.Join(", ", missingProperties.Select(p => $"'{p}'"))} {propertyOrProperties} could be found on the resource in order to perform authorization. Should a different authorization strategy be used?";
        }

        if (notAuthorizedProperties != null && notAuthorizedProperties.Any())
        {
            string edOrgIdsFromFilters = string.Join(", ", authorizationFilters.Select(x => $"'{x.Value}'"));
            string notAuthorizedMessage =
                $"No relationships have been established between the caller's education organization id claims ({edOrgIdsFromFilters}) and the resource item's {notAuthorizedProperties.First()} value.";
            if (notAuthorizedProperties.Count() > 1)
            {
                notAuthorizedMessage =
                    $"No relationships have been established between the caller's education organization id claims ({edOrgIdsFromFilters}) and one or more of the following properties of the resource item: {string.Join(", ", notAuthorizedProperties.Select(p => $"'{p}'"))}.";
            }
            return notAuthorizedMessage;
        }

        return string.Empty;
    }

    public static ResourceAuthorizationResult BuildResourceAuthorizationResult(
        AuthorizationResult authorizationResult,
        AuthorizationFilter[] authorizationFilters
    )
    {
        var missingProperties = new List<string>();
        var notAuthorizedProperties = new List<string>();

        switch (authorizationResult)
        {
            case AuthorizationResult.MissingProperty missingProperty:
                missingProperties.AddRange(missingProperty.PropertyNames);
                break;
            case AuthorizationResult.NotAuthorized notAuthorized:
                notAuthorizedProperties.AddRange(notAuthorized.PropertyNames);
                break;
        }

        if (missingProperties.Count != 0 || notAuthorizedProperties.Count != 0)
        {
            return new ResourceAuthorizationResult.NotAuthorized(
                [BuildErrorMessage(authorizationFilters, missingProperties, notAuthorizedProperties)]
            );
        }

        return new ResourceAuthorizationResult.Authorized();
    }

    private static bool IsAuthorized(AuthorizationFilter[] authorizationFilters, long[]? educationOrgIds)
    {
        var authorizedEdOrgIds = authorizationFilters
            .Select(filter =>
                long.TryParse(filter.Value, out long filterEdOrgId) ? filterEdOrgId : (long?)null
            )
            .WhereNotNull()
            .ToHashSet();

        return educationOrgIds != null
            && educationOrgIds.Length > 0
            && authorizedEdOrgIds.Any(id => educationOrgIds.Contains(id));
    }

    public static async Task<AuthorizationResult> ValidateEdOrgAuthorization(
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
            return new AuthorizationResult.MissingProperty(["EducationOrganizationId"]);
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
        bool isAuthorized = Array.TrueForAll(
            requestEdOrgHierarchies,
            requestEdOrgIdHierarchy =>
                requestEdOrgIdHierarchy.Any(edOrgId => filterEdOrgIds.Contains(edOrgId))
        );

        if (!isAuthorized)
        {
            return new AuthorizationResult.NotAuthorized(
                securityElements.EducationOrganization.Select(e => e.PropertyName.Value).Distinct().ToArray(),
                ""
            );
        }
        return new AuthorizationResult.Authorized();
    }

    public static async Task<AuthorizationResult> ValidateStudentAuthorization(
        IAuthorizationRepository authorizationRepository,
        DocumentSecurityElements securityElements,
        AuthorizationFilter[] authorizationFilters
    )
    {
        string propertyName = "StudentUniqueId";
        if (securityElements.Student.Length == 0)
        {
            return new AuthorizationResult.MissingProperty([propertyName]);
        }
        string studentUniqueId = securityElements.Student[0].Value;
        long[] educationOrgIds = await authorizationRepository.GetEducationOrganizationsForStudent(
            studentUniqueId
        );
        bool isAuthorized = IsAuthorized(authorizationFilters, educationOrgIds);
        if (!isAuthorized)
        {
            return new AuthorizationResult.NotAuthorized(
                [propertyName],
                "Hint: You may need to create a corresponding 'StudentSchoolAssociation' item."
            );
        }

        return new AuthorizationResult.Authorized();
    }

    public static async Task<AuthorizationResult> ValidateStudentResponsibilityAuthorization(
        IAuthorizationRepository authorizationRepository,
        DocumentSecurityElements securityElements,
        AuthorizationFilter[] authorizationFilters
    )
    {
        string propertyName = "StudentUniqueId";
        if (securityElements.Student.Length == 0)
        {
            return new AuthorizationResult.MissingProperty([propertyName]);
        }
        string studentUniqueId = securityElements.Student[0].Value;
        long[] educationOrgIds =
            await authorizationRepository.GetEducationOrganizationsForStudentResponsibility(studentUniqueId);
        bool isAuthorized = IsAuthorized(authorizationFilters, educationOrgIds);
        if (!isAuthorized)
        {
            return new AuthorizationResult.NotAuthorized(
                [propertyName],
                "Hint: You may need to create a corresponding 'StudentEducationOrganizationResponsibilityAssociation' item."
            );
        }

        return new AuthorizationResult.Authorized();
    }

    public static async Task<AuthorizationResult> ValidateContactAuthorization(
        IAuthorizationRepository authorizationRepository,
        DocumentSecurityElements securityElements,
        AuthorizationFilter[] authorizationFilters
    )
    {
        string propertyName = "ContactUniqueId";
        if (securityElements.Contact.Length == 0)
        {
            return new AuthorizationResult.MissingProperty([propertyName]);
        }
        string contactUniqueId = securityElements.Contact[0].Value;

        long[] educationOrgIds = await authorizationRepository.GetEducationOrganizationsForContact(
            contactUniqueId
        );

        bool isAuthorized = IsAuthorized(authorizationFilters, educationOrgIds);

        if (!isAuthorized)
        {
            return new AuthorizationResult.NotAuthorized(
                [propertyName],
                "Hint: You may need to create corresponding 'StudentSchoolAssociation' and 'StudentContactAssociation' items."
            );
        }

        return new AuthorizationResult.Authorized();
    }

    public static async Task<AuthorizationResult> ValidateStaffAuthorization(
        IAuthorizationRepository authorizationRepository,
        DocumentSecurityElements securityElements,
        AuthorizationFilter[] authorizationFilters
    )
    {
        string propertyName = "StaffUniqueId";
        if (securityElements.Staff.Length == 0)
        {
            return new AuthorizationResult.MissingProperty([propertyName]);
        }
        string staffUniqueId = securityElements.Staff[0].Value;
        long[] educationOrgIds = await authorizationRepository.GetEducationOrganizationsForStaff(
            staffUniqueId
        );
        bool isAuthorized = IsAuthorized(authorizationFilters, educationOrgIds);
        if (!isAuthorized)
        {
            return new AuthorizationResult.NotAuthorized(
                [propertyName],
                "Hint: You may need to create corresponding 'StaffEducationOrganizationEmploymentAssociation' or 'StaffEducationOrganizationAssignmentAssociation' items."
            );
        }
        return new AuthorizationResult.Authorized();
    }
}

public abstract record AuthorizationResult
{
    public record Authorized() : AuthorizationResult;

    public record NotAuthorized(string[] PropertyNames, string Hint) : AuthorizationResult;

    public record MissingProperty(string[] PropertyNames) : AuthorizationResult;
}
