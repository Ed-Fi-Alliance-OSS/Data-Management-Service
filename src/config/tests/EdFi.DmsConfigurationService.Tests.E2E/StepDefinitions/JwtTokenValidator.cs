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
                    var scopes = scopeClaim.Value.Split(' ');
                    return scopes.Contains(requiredScope);
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
                    var namespaces = namespacesClaim.Value.Split(' ');
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
}
