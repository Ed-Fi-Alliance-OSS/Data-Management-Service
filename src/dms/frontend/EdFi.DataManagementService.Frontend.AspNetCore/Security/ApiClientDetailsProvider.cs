// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Security;

public interface IApiClientDetailsProvider
{
    ApiClientDetails RetrieveApiClientDetailsFromToken(string jwtTokenHashCode, IList<Claim> claims);
}

public class ApiClientDetailsProvider() : IApiClientDetailsProvider
{
    public ApiClientDetails RetrieveApiClientDetailsFromToken(string jwtTokenHashCode, IList<Claim> claims)
    {
        var requiredClaims = claims
            .Where(c => c.Type == "scope" || c.Type == "jti")
            .ToDictionary(c => c.Type, c => c.Value);
        var claimSetName = requiredClaims.TryGetValue("scope", out string? value) ? value : string.Empty;
        var tokenId = GetTokenId(requiredClaims, jwtTokenHashCode);
        var apiClientDetails = new ApiClientDetails(tokenId, claimSetName, [], []);
        return apiClientDetails;
    }

    private static string GetTokenId(Dictionary<string, string> claims, string jwtTokenHashCode)
    {
        return claims.TryGetValue("jti", out string? value) ? value : jwtTokenHashCode;
    }
}
