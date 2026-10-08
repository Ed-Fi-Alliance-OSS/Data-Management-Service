// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// A JWT validation double that knows a fixed set of tokens, each mapped to its own client.
/// </summary>
/// <remarks>
/// <see cref="FakeJwtValidationService"/> returns one client for every token, which cannot tell two
/// clients apart. A token this table does not know returns <c>(null, null)</c>, which is what the
/// production service returns for a token it rejects, so the host answers a real <c>401</c> rather
/// than a stubbed one.
/// </remarks>
internal sealed class PerTokenJwtValidationService(
    IReadOnlyDictionary<string, ClientAuthorizations> authorizationsByToken
) : IJwtValidationService
{
    public Task<(ClaimsPrincipal?, ClientAuthorizations?)> ValidateAndExtractClientAuthorizationsAsync(
        string token,
        CancellationToken cancellationToken
    ) => Task.FromResult(Lookup(token));

    public Task<(ClaimsPrincipal?, ClientAuthorizations?)> ValidateAndExtractClientAuthorizationsAsync(
        string authorizationHeader,
        int tokenStartIndex,
        CancellationToken cancellationToken
    ) => Task.FromResult(Lookup(authorizationHeader[tokenStartIndex..]));

    private (ClaimsPrincipal?, ClientAuthorizations?) Lookup(string token)
    {
        if (!authorizationsByToken.TryGetValue(token, out ClientAuthorizations? authorizations))
        {
            return (null, null);
        }

        ClaimsPrincipal principal = new(
            new ClaimsIdentity([new Claim("client_id", authorizations.ClientId)], "test")
        );

        return (principal, authorizations);
    }
}
