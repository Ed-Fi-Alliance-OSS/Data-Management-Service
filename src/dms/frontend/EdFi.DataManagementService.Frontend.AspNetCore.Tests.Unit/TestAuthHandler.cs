// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using System.Text.Encodings.Web;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<IdentitySettings> identitySettings
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim("client_id", AuthenticationConstants.ClientId),
            new Claim(identitySettings.Value.RoleClaimType, identitySettings.Value.ClientRole),
        };

        var identity = new ClaimsIdentity(claims, AuthenticationConstants.AuthenticationSchema);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AuthenticationConstants.AuthenticationSchema);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
