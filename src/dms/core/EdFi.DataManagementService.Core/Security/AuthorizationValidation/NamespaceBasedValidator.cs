// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationValidation;

/// <summary>
/// Validates the authorization strategy that performs namespace based
/// authorization.
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyName)]
public class NamespaceBasedValidator : IAuthorizationValidator
{
    private const string AuthorizationStrategyName = "NamespaceBased";

    public AuthorizationResult ValidateAuthorization(
        DocumentSecurityElements securityElements,
        ClientAuthorizations authorizations
    )
    {
        List<NamespacePrefix>? namespacePrefixesFromClaim = authorizations.NamespacePrefixes;
        string[]? namespacesFromRequest = securityElements.Namespace;

        if (namespacesFromRequest.Length == 0)
        {
            string error =
                "No 'Namespace' (or Namespace-suffixed) property could be found on the resource in order to perform authorization. Should a different authorization strategy be used?";
            return new AuthorizationResult(false, error);
        }
        if (namespacePrefixesFromClaim.Count == 0)
        {
            string noRequiredClaimError =
                $"The API client has been given permissions on a resource that uses the '{AuthorizationStrategyName}' authorization strategy but the client doesn't have any namespace prefixes assigned.";
            return new AuthorizationResult(false, noRequiredClaimError);
        }
        bool allMatching = namespacesFromRequest
            .ToList()
            .TrueForAll(fromRequest =>
                namespacePrefixesFromClaim.Exists(fromClaim =>
                    fromRequest.StartsWith(fromClaim.Value, StringComparison.InvariantCultureIgnoreCase)
                )
            );

        if (!allMatching)
        {
            string claimNamespacePrefixes = string.Join(
                ", ",
                namespacePrefixesFromClaim.Select(x => $"'{x.Value}'")
            );
            return new AuthorizationResult(
                false,
                $"Access to the resource item could not be authorized based on the caller's NamespacePrefix claims: {claimNamespacePrefixes}."
            );
        }
        return new AuthorizationResult(true);
    }
}
