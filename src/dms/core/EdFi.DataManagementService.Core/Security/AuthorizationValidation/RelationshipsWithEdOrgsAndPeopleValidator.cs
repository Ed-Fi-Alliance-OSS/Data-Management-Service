// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
        var errorMessages = new List<string>();
        if (
            RelationshipsBasedAuthorizationHelper.HasSecurable(
                authorizationSecurableInfos,
                SecurityElementNameConstants.StudentUniqueId
            )
        )
        {
            var studentResult = await RelationshipsBasedAuthorizationHelper.ValidateStudentAuthorization(
                authorizationRepository,
                securityElements,
                authorizationFilters
            );
            if (studentResult is ResourceAuthorizationResult.NotAuthorized notAuthorizedStudent)
            {
                errorMessages.AddRange(notAuthorizedStudent.ErrorMessages);
            }
        }

        if (
            RelationshipsBasedAuthorizationHelper.HasSecurable(
                authorizationSecurableInfos,
                SecurityElementNameConstants.ContactUniqueId
            )
        )
        {
            var contactResult = await RelationshipsBasedAuthorizationHelper.ValidateContactAuthorization(
                authorizationRepository,
                securityElements,
                authorizationFilters
            );
            if (contactResult is ResourceAuthorizationResult.NotAuthorized notAuthorizedContact)
            {
                errorMessages.AddRange(notAuthorizedContact.ErrorMessages);
            }
        }

        // Return consolidated result
        if (errorMessages.Count > 0)
        {
            return new ResourceAuthorizationResult.NotAuthorized([.. errorMessages]);
        }

        return new ResourceAuthorizationResult.Authorized();
    }
}
