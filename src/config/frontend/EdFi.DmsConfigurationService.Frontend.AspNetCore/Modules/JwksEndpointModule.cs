// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
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

    private IResult GetJwksConfiguration(IOptions<IdentitySettings> identitySettings)
    {
        // Use the signing key from settings to create JWKS
        var signingKey = identitySettings.Value.SigningKey;

        if (string.IsNullOrEmpty(signingKey))
        {
            // If no signing key is available, return an empty JWKS
            return Results.Ok(new { keys = Array.Empty<object>() });
        }

        try
        {
            // Create RSA key from the base64 encoded key
            using var rsa = RSA.Create();

            // The signing key is stored as base64 in the settings
            var keyBytes = Convert.FromBase64String(signingKey);
            rsa.ImportRSAPrivateKey(keyBytes, out _);

            // Export the RSA public key parameters
            var rsaParameters = rsa.ExportParameters(false);

            // Create JSON Web Key from RSA parameters
            var jwk = new JsonWebKey
            {
                Kty = "RSA",
                Use = "sig",
                Kid = CreateKeyId(rsaParameters),
                E = Base64UrlEncoder.Encode(rsaParameters.Exponent ?? Array.Empty<byte>()),
                N = Base64UrlEncoder.Encode(rsaParameters.Modulus ?? Array.Empty<byte>()),
                Alg = "RS256"
            };

            // Return JWKS
            var jwks = new { keys = new[] { jwk } };
            return Results.Ok(jwks);
        }
        catch (Exception ex)
        {
            // In case of errors parsing the key, log and return empty JWKS
            // In production, errors should be properly logged
            Console.WriteLine($"Error creating JWKS: {ex.Message}");
            return Results.Ok(new { keys = Array.Empty<object>() });
        }
    }

    // Create a key ID from the RSA parameters
    private static string CreateKeyId(RSAParameters parameters)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(parameters.Modulus ?? Array.Empty<byte>());
        return Base64UrlEncoder.Encode(hash.Take(8).ToArray());
    }
}
