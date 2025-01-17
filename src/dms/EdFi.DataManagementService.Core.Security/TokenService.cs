// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security.Model;

namespace EdFi.DataManagementService.Core.Security;

public interface ITokenService
{
    ApiClientDetails ProcessAndCacheToken(string jwtToken);
}

public class TokenService(ITokenProcessor tokenProcessor, ApiClientDetailsCache apiClientDetailsCache)
    : ITokenService
{
    public ApiClientDetails ProcessAndCacheToken(string jwtToken)
    {
        var tokenId = GetTokenId(jwtToken);
        var cachedValues = apiClientDetailsCache.GetCachedApiDetails(tokenId);

        if (cachedValues != null)
        {
            return cachedValues;
        }

        var tokenValues = tokenProcessor.DecodeToken(jwtToken);
        var apiClientDetails = new ApiClientDetails(
            tokenId,
            tokenValues["scope"].ToString(),
            [29901],
            ["uri://ed-fi.org"],
            DateTime.UtcNow.AddMinutes(30)
        );

        apiClientDetailsCache.CacheApiDetails(tokenId, apiClientDetails, TimeSpan.FromMinutes(30));

        return apiClientDetails;
    }

    private string GetTokenId(string jwtToken)
    {
        var claims = tokenProcessor.DecodeToken(jwtToken);
        return claims.ContainsKey("jti") ? claims["jti"] : jwtToken.GetHashCode().ToString();
    }
}
