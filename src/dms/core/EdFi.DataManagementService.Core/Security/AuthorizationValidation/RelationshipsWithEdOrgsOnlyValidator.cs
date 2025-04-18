// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationValidation;

/// <summary>
/// validates whether a client is authorized to access a resource based on relationships with education organizations.
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyName)]
public class RelationshipsWithEdOrgsOnlyValidator(IAuthorizationRepository authorizationRepository)
    : IAuthorizationValidator
{
    private const string AuthorizationStrategyName = "RelationshipsWithEdOrgsOnly";

    public async Task<ResourceAuthorizationResult> ValidateAuthorization(
        DocumentSecurityElements securityElements,
        AuthorizationFilter[] authorizationFilters,
        AuthorizationSecurableInfo[] authorizationSecurableInfos,
        OperationType operationType
    )
    {
        List<EducationOrganizationId> edOrgsFromRequest = securityElements
            .EducationOrganization.Select(e => e.Id)
            .ToList();

        if (!edOrgsFromRequest.Any())
        {
            string error =
                "No 'EducationOrganizationIds' property could be found on the resource in order to perform authorization. Should a different authorization strategy be used?";
            return new ResourceAuthorizationResult.NotAuthorized([error]);
        }

        // Retrieve the hierarchy of the education organization ids from the request
        var edOrgIdValues = edOrgsFromRequest.Select(e => e.Value).ToArray();

        var ancestorEducationOrganizationIds =
            await authorizationRepository.GetAncestorEducationOrganizationIds(edOrgIdValues);

        bool isAuthorized = authorizationFilters
            .Select(filter => long.TryParse(filter.Value, out var edOrgId) ? edOrgId : (long?)null)
            .Where(edOrgId => edOrgId.HasValue)
            .Any(edOrgId =>
                edOrgId != null && ancestorEducationOrganizationIds.ToList().Contains(edOrgId.Value)
            );

        if (!isAuthorized)
        {
            string edOrgIdsFromFilters = string.Join(", ", authorizationFilters.Select(x => $"'{x.Value}'"));
            string error =
                (operationType == OperationType.Get || operationType == OperationType.Delete)
                    ? $"Access to the resource item could not be authorized based on the caller's EducationOrganizationIds claims: {edOrgIdsFromFilters}."
                    : $"No relationships have been established between the caller's education organization id claims ({edOrgIdsFromFilters}) and properties of the resource item.";
            return new ResourceAuthorizationResult.NotAuthorized([error]);
        }
        return new ResourceAuthorizationResult.Authorized();
    }
}
