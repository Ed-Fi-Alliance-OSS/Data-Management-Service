// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;

namespace EdFi.DataManagementService.Core.Security;

public interface ITokenProcessor
{
    IDictionary<string, string> DecodeToken(string token);
}

public class TokenProcessor : ITokenProcessor
{
    public IDictionary<string, string> DecodeToken(string token)
    {
        var handler = new JwtSecurityTokenHandler();

        if (!handler.CanReadToken(token))
        {
            throw new ArgumentException("Invalid token format.");
        }

        var decodedToken = handler.ReadJwtToken(token);

        var claims = decodedToken
            .Claims.Where(c => c.Type == "scope" || c.Type == "jti")
            .ToDictionary(c => c.Type, c => c.Value);

        return claims;
    }
}
