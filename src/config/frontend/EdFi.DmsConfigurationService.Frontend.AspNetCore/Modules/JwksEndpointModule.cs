// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class JwksEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/.well-known/jwks.json", GetJwksConfiguration);
    }

    private IResult GetJwksConfiguration(HttpContext httpContext, ITokenManager tokenManager)
    {
        // Fetch public keys from the token manager (database-backed)
        var publicKeys = tokenManager.GetPublicKeys();
        if (publicKeys == null || !publicKeys.Any())
        {
            return Results.Ok(new { keys = Array.Empty<object>() });
        }

        var jwks = new
        {
            keys = publicKeys.Select(pk => new JsonWebKey
            {
                Kty = "RSA",
                Use = "sig",
                Kid = pk.KeyId,
                E = Base64UrlEncoder.Encode(pk.RsaParameters.Exponent ?? Array.Empty<byte>()),
                N = Base64UrlEncoder.Encode(pk.RsaParameters.Modulus ?? Array.Empty<byte>()),
                Alg = "RS256",
            }).ToArray()
        };
        return Results.Ok(jwks);
    }

    // Create a key ID from the RSA parameters
    private static string CreateKeyId(RSAParameters parameters)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(parameters.Modulus ?? Array.Empty<byte>());
        return Base64UrlEncoder.Encode(hash.Take(8).ToArray());
    }
}
