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
    /// <summary>
    /// Optional request header that overrides the authenticated principal's <c>client_id</c> claim,
    /// letting a single test run simulate more than one calling client.
    /// </summary>
    public const string ClientIdHeaderName = "X-Test-ClientId";

    /// <summary>
    /// Optional request header that suppresses the <c>IdentitySettings:ConfigServiceRole</c>
    /// claim, yielding an authenticated principal shaped like an ordinary client-credentials
    /// token. Absent the header the role claim is emitted as before.
    /// </summary>
    public const string OmitRoleClaimHeaderName = "X-Test-OmitRoleClaim";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Extract scope from the request header for testing
        var scopeHeader = Context.Request.Headers["X-Test-Scope"].ToString();
        if (string.IsNullOrEmpty(scopeHeader))
        {
            return Task.FromResult(AuthenticateResult.Fail("Scope header is missing."));
        }

        // Tests that need to act as a caller other than the configured client (for example, to
        // prove that /connect/revoke refuses to revoke another client's token) set this header.
        // Absent the header the previous hardcoded client_id is used, so existing tests are unaffected.
        var clientIdHeader = Context.Request.Headers[ClientIdHeaderName].ToString();
        string clientId = string.IsNullOrEmpty(clientIdHeader)
            ? identitySettings.Value.ClientId
            : clientIdHeader;

        List<Claim> claims = [new Claim("client_id", clientId), new Claim("scope", scopeHeader)];

        // Only tests that deliberately need a role-less caller set this header, so the claim set
        // is unchanged for every other test.
        if (string.IsNullOrEmpty(Context.Request.Headers[OmitRoleClaimHeaderName].ToString()))
        {
            claims.Add(
                new Claim(identitySettings.Value.RoleClaimType, identitySettings.Value.ConfigServiceRole)
            );
        }

        var identity = new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, JwtBearerDefaults.AuthenticationScheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
