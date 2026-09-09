// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;

namespace EdFi.DmsConfigurationService.Tests.E2E.StepDefinitions;

public static class JwtTokenValidator
{
    public static bool ValidateClaimset(string token, string requiredScope)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            if (tokenHandler.CanReadToken(token))
            {
                var jwtToken = tokenHandler.ReadJwtToken(token);
                var scopeClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "scope");
                if (scopeClaim != null)
                {
                    var scopes = scopeClaim.Value.Split(
                        new[] { ' ', ',' },
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    );
                    return scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase);
                }
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool ValidateNamespace(string token, string namespacPrefix)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            if (tokenHandler.CanReadToken(token))
            {
                var jwtToken = tokenHandler.ReadJwtToken(token);
                var namespacesClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "namespacePrefixes");
                if (namespacesClaim != null)
                {
                    var namespaces = namespacesClaim.Value.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    );
                    return namespaces.Contains(namespacPrefix);
                }
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the dataStoreIds claim, distinguishing a claim that is present and empty from a claim
    /// the identity provider never emitted.
    /// </summary>
    public static bool TryGetDataStoreIdsClaim(string token, out string dataStoreIds)
    {
        dataStoreIds = string.Empty;
        var tokenHandler = new JwtSecurityTokenHandler();
        if (!tokenHandler.CanReadToken(token))
        {
            return false;
        }

        var claim = tokenHandler.ReadJwtToken(token).Claims.FirstOrDefault(c => c.Type == "dataStoreIds");
        if (claim is null)
        {
            return false;
        }

        dataStoreIds = claim.Value;
        return true;
    }

    public static bool ValidateEdOrgIds(string token, string edOrgIds)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            if (tokenHandler.CanReadToken(token))
            {
                var jwtToken = tokenHandler.ReadJwtToken(token);
                var edOrgIdsClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "educationOrganizationIds");
                if (edOrgIdsClaim != null)
                {
                    var edOrgIdList = edOrgIdsClaim.Value.Split(',');
                    return edOrgIdList.Contains(edOrgIds);
                }
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
