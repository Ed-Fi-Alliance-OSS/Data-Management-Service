// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationValidation;

/// <summary>
/// Validates whether a client is authorized to access a resource based on relationships
/// with education organizations and people.
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyName)]
public class RelationshipsWithEdOrgsAndPeopleValidator(IAuthorizationRepository authorizationRepository)
    : IAuthorizationValidator
{
    private const string AuthorizationStrategyName = "RelationshipsWithEdOrgsAndPeople";

    public async Task<ResourceAuthorizationResult> ValidateAuthorization(
        DocumentSecurityElements securityElements,
        AuthorizationFilter[] authorizationFilters,
        AuthorizationSecurableInfo[] authorizationSecurableInfos,
        OperationType operationType
    )
    {
        bool isStudentSecurable = authorizationSecurableInfos
            .AsEnumerable()
            .Any(x => x.SecurableKey == SecurityElementNameConstants.StudentUniqueId);

        bool isContactSecurable = authorizationSecurableInfos
            .AsEnumerable()
            .Any(x => x.SecurableKey == SecurityElementNameConstants.ContactUniqueId);

        if (isStudentSecurable)
        {
            if (securityElements.Student.Length == 0)
            {
                string error =
                    "No 'Student' property could be found on the resource in order to perform authorization. Should a different authorization strategy be used?";
                return new ResourceAuthorizationResult.NotAuthorized([error]);
            }

            var studentUniqueId = securityElements.Student[0].Value;

            var educationOrgIds = await authorizationRepository.GetEducationOrganizationsForStudent(
                studentUniqueId
            );

            var authorizedEdOrgIds = authorizationFilters
                .Select(f => long.TryParse(f.Value, out var id) ? (long?)id : null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value);

            bool isAuthorized =
                educationOrgIds != null
                && educationOrgIds.Length > 0
                && authorizedEdOrgIds.Any(id => educationOrgIds.Contains(id));

            if (!isAuthorized)
            {
                string edOrgIdsFromFilters = string.Join(
                    ", ",
                    authorizationFilters.Select(x => $"'{x.Value}'")
                );
                string error =
                    $"No relationships have been established between the caller's education organization id claims ({edOrgIdsFromFilters}) and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StudentUniqueId'.";
                return new ResourceAuthorizationResult.NotAuthorized([error]);
            }
        }

        if (isContactSecurable)
        {
            if (securityElements.Contact.Length == 0)
            {
                string error =
                    "No 'Contact' property could be found on the resource in order to perform authorization. Should a different authorization strategy be used?";
                return new ResourceAuthorizationResult.NotAuthorized([error]);
            }
            var contactUniqueId = securityElements.Contact[0].Value;
            var educationOrgIds = await authorizationRepository.GetEducationOrganizationsForContact(
                contactUniqueId
            );
            var authorizedEdOrgIds = authorizationFilters
                .Select(f => long.TryParse(f.Value, out var id) ? (long?)id : null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value);
            bool isAuthorized =
                educationOrgIds != null
                && educationOrgIds.Length > 0
                && authorizedEdOrgIds.Any(id => educationOrgIds.Contains(id));
            if (!isAuthorized)
            {
                string edOrgIdsFromFilters = string.Join(
                    ", ",
                    authorizationFilters.Select(x => $"'{x.Value}'")
                );
                string error =
                    $"No relationships have been established between the caller's education organization id claims ({edOrgIdsFromFilters}) and one or more of the following properties of the resource item: 'EducationOrganizationId', 'ContactUniqueId'.";
                return new ResourceAuthorizationResult.NotAuthorized([error]);
            }
        }

        return new ResourceAuthorizationResult.Authorized();
    }
}
