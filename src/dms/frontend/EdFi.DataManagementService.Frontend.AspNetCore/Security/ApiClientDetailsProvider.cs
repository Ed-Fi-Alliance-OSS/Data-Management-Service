// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Security;

public interface IApiClientDetailsProvider
{
    ApiClientDetails RetrieveApiClientDetailsFromToken(string jwtToken);
}

public class ApiClientDetailsProvider(ITokenProcessor tokenProcessor) : IApiClientDetailsProvider
{
    public ApiClientDetails RetrieveApiClientDetailsFromToken(string jwtToken)
    {
        var tokenId = GetTokenId(jwtToken);
        var tokenValues = tokenProcessor.DecodeToken(jwtToken);
        var apiClientDetails = new ApiClientDetails(
            tokenId,
            tokenValues["scope"].ToString(),
            [29901],
            ["uri://ed-fi.org"]
        );
        return apiClientDetails;
    }

    private string GetTokenId(string jwtToken)
    {
        var claims = tokenProcessor.DecodeToken(jwtToken);
        return claims.TryGetValue("jti", out string? value) ? value : jwtToken.GetHashCode().ToString();
    }
}
