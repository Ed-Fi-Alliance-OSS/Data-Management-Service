// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using System.Text.Encodings.Web;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit;

public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<IdentitySettings> identitySettings
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Extract scope from the request header for testing
        var scopeHeader = Context.Request.Headers["X-Test-Scope"].ToString();
        if (string.IsNullOrEmpty(scopeHeader))
        {
            return Task.FromResult(AuthenticateResult.Fail("Scope header is missing."));
        }

        // Allow a test to simulate a caller with a client_id different from the default, e.g. to
        // exercise ownership checks. When the header is absent, behavior is unchanged.
        var clientIdHeader = Context.Request.Headers["X-Test-ClientId"].ToString();
        var clientId = string.IsNullOrEmpty(clientIdHeader)
            ? identitySettings.Value.ClientId
            : clientIdHeader;

        var claims = new[]
        {
            new Claim("client_id", clientId),
            new Claim(identitySettings.Value.RoleClaimType, identitySettings.Value.ConfigServiceRole),
            new Claim("scope", scopeHeader),
        };

        var identity = new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, JwtBearerDefaults.AuthenticationScheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
