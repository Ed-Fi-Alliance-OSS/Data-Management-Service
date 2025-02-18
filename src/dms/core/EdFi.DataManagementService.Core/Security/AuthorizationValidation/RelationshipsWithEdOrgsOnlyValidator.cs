// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationValidation;

/// <summary>
/// Validates the authorization strategy that performs RelationshipsWithEdOrgsOnly authorization.
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyName)]
public class RelationshipsWithEdOrgsOnlyValidator : IAuthorizationValidator
{
    private const string AuthorizationStrategyName = "RelationshipsWithEdOrgsOnly";

    public AuthorizationResult ValidateAuthorization(
        DocumentSecurityElements securityElements,
        ApiClientDetails details
    )
    {
        var edOrgIdsFromClaim = details.EducationOrganizationIds;
        var edOrgsFromRequest = securityElements.EducationOrganization;

        if (edOrgsFromRequest.Length == 0)
        {
            var error =
                "No 'EducationOrganizationIds' property could be found on the resource in order to perform authorization. Should a different authorization strategy be used?";
            return new AuthorizationResult(false, error);
        }
        if (edOrgIdsFromClaim.Count == 0)
        {
            var noRequiredClaimError =
                $"The API client has been given permissions on a resource that uses the '{AuthorizationStrategyName}' authorization strategy but the client doesn't have any education organizations assigned.";
            return new AuthorizationResult(false, noRequiredClaimError);
        }
        var allMatching = edOrgsFromRequest
            .ToList()
            .TrueForAll(fromRequest =>
                edOrgIdsFromClaim.Exists(fromClaim =>
                    fromRequest.Equals(fromClaim.Value, StringComparison.InvariantCultureIgnoreCase)
                )
            );

        if (!allMatching)
        {
            string claimEdOrgIds = string.Join("', '", edOrgIdsFromClaim.Select(x => x.Value));
            return new AuthorizationResult(
                false,
                $"No relationships have been established between the caller's education organization id claims ('{claimEdOrgIds}') and properties of the resource item."
            );
        }
        return new AuthorizationResult(true);
    }
}
