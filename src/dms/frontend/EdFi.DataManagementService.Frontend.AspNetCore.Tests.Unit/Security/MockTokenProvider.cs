// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Security;

public static class MockTokenProvider
{
    public static string GenerateJwtToken(string issuer, string audience, Dictionary<string, string> claims)
    {
        var jwtClaims = claims.Select(c => new Claim(c.Key, c.Value)).ToArray();
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: jwtClaims,
            expires: DateTime.Now.AddMinutes(30)
        );
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
