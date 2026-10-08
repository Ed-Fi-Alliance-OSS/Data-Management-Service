// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Endpoint metadata marking a route that authenticates its caller itself and must not have an
/// <c>Authorization: Bearer</c> header processed by the JWT bearer handler. <c>UseAuthentication</c>
/// authenticates every request with the default scheme whatever the endpoint requires, so leaving
/// out <c>RequireAuthorization()</c> does not keep the handler away. Only <c>/connect/revoke</c>
/// carries it (DMS-1327 D-03, D-07.4): the handler's key resolver loads keys through the JWKS helper,
/// which creates a development certificate when the file is missing, and revocation would then verify
/// the token against that unrelated key and answer an empty 200 without revoking.
/// </summary>
internal sealed class BearerAuthenticationExemptMetadata
{
    public static readonly BearerAuthenticationExemptMetadata Instance = new();

    private BearerAuthenticationExemptMetadata() { }

    /// <summary>
    /// The <see cref="JwtBearerEvents.OnMessageReceived"/> hook: on a marked route the handler stops
    /// with no result before it reads or validates the token, so the request continues anonymous.
    /// </summary>
    public static Task SkipOnMarkedEndpoint(MessageReceivedContext context)
    {
        if (
            context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<BearerAuthenticationExemptMetadata>()
            is not null
        )
        {
            context.NoResult();
        }

        return Task.CompletedTask;
    }
}
