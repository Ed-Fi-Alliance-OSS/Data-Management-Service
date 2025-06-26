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
    private const string AuthorizationStrategyName =
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople;

    public async Task<ResourceAuthorizationResult> ValidateAuthorization(
        DocumentSecurityElements securityElements,
        AuthorizationFilter[] authorizationFilters,
        AuthorizationSecurableInfo[] authorizationSecurableInfos,
        OperationType operationType
    )
    {
        var missingProperties = new List<string>();
        var notAuthorizedProperties = new List<string>();

        if (
            RelationshipsBasedAuthorizationHelper.HasSecurable(
                authorizationSecurableInfos,
                SecurityElementNameConstants.EducationOrganization
            )
        )
        {
            var edOrgResult = await RelationshipsBasedAuthorizationHelper.ValidateEdOrgAuthorization(
                authorizationRepository,
                securityElements,
                authorizationFilters,
                operationType
            );
            if (edOrgResult.Type == AuthorizationResultType.MissingProperty)
            {
                missingProperties.AddRange(edOrgResult.PropertyNames);
            }
            else if (edOrgResult.Type == AuthorizationResultType.NotAuthorized)
            {
                notAuthorizedProperties.AddRange(edOrgResult.PropertyNames);
            }
        }

        // Validate authorization for each type of person
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
            if (studentResult.Type == AuthorizationResultType.MissingProperty)
            {
                missingProperties.AddRange(studentResult.PropertyNames);
            }
            else if (studentResult.Type == AuthorizationResultType.NotAuthorized)
            {
                notAuthorizedProperties.AddRange(studentResult.PropertyNames);
            }
        }

        if (
            RelationshipsBasedAuthorizationHelper.HasSecurable(
                authorizationSecurableInfos,
                SecurityElementNameConstants.StaffUniqueId
            )
        )
        {
            var staffResult = await RelationshipsBasedAuthorizationHelper.ValidateStaffAuthorization(
                authorizationRepository,
                securityElements,
                authorizationFilters
            );
            if (staffResult.Type == AuthorizationResultType.MissingProperty)
            {
                missingProperties.AddRange(staffResult.PropertyNames);
            }
            else if (staffResult.Type == AuthorizationResultType.NotAuthorized)
            {
                notAuthorizedProperties.AddRange(staffResult.PropertyNames);
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
            if (contactResult.Type == AuthorizationResultType.MissingProperty)
            {
                missingProperties.AddRange(contactResult.PropertyNames);
            }
            else if (contactResult.Type == AuthorizationResultType.NotAuthorized)
            {
                notAuthorizedProperties.AddRange(contactResult.PropertyNames);
            }
        }

        if (missingProperties.Count != 0 || notAuthorizedProperties.Count != 0)
        {
            var errorMessage = RelationshipsBasedAuthorizationHelper.BuildErrorMessage(
                authorizationFilters,
                missingProperties,
                notAuthorizedProperties
            );
            return new ResourceAuthorizationResult.NotAuthorized([errorMessage]);
        }

        return new ResourceAuthorizationResult.Authorized();
    }
}
