// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddictIdentityOptions = EdFi.DmsConfigurationService.Backend.OpenIddict.Models.IdentityOptions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class JwksEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/.well-known/jwks.json", GetJwksConfiguration);
    }

    /// <summary>
    /// Publishes the keys of the shared signing-key snapshot (spec §4.7). A snapshot with no keys is a successful
    /// retrieval of none and is served as the empty key set. With no usable snapshot the answer is the dependency 503,
    /// never an empty key set: a failed retrieval is not published as empty (I-8).
    /// </summary>
    private static async Task<IResult> GetJwksConfiguration(
        HttpContext httpContext,
        ISigningKeySnapshotProvider signingKeyProvider,
        IOptions<OpenIddictIdentityOptions> identityOptions,
        ILogger<JwksEndpointModule> logger
    )
    {
        SigningKeySnapshot snapshot;
        try
        {
            snapshot = await signingKeyProvider.GetUsableAsync(httpContext.RequestAborted);
        }
        catch (SigningKeysUnavailableException exception)
        {
            logger.LogError(
                exception,
                "The JWKS could not be served: the {Category} is unavailable (trace {TraceId})",
                exception.Category,
                httpContext.TraceIdentifier
            );
            await SigningKeyDependencyResponse.WriteAsync(
                httpContext,
                SigningKeyDependencyResponse.RetryAfterSeconds(
                    SigningKeySettings.FromIdentityOptions(identityOptions.Value)
                )
            );
            return Results.Empty;
        }

        var jwks = new
        {
            keys = snapshot
                .Keys.Select(entry =>
                {
                    RSAParameters parameters = entry.PublicParameters;
                    return new JsonWebKey
                    {
                        Kty = "RSA",
                        Use = "sig",
                        Kid = entry.KeyId,
                        E = Base64UrlEncoder.Encode(parameters.Exponent),
                        N = Base64UrlEncoder.Encode(parameters.Modulus),
                        Alg = "RS256",
                    };
                })
                .ToArray(),
        };
        return Results.Ok(jwks);
    }
}
