// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Authorization;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;

/// <summary>
/// A custom authorization handler that validates if the user has the required scopes
/// based on the claims in their JWT token.
/// ScopePolicyHandler is responsible for enforcing the authorization policies created using ScopePolicy.
/// </summary>
public class ScopePolicyHandler : AuthorizationHandler<ScopePolicy>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopePolicy requirement
    )
    {
        // A token's scope may be a space-delimited string or an array; an array arrives as one "scope" claim per
        // element, so every scope claim is considered, and each is split on ASCII spaces. The claim type is matched
        // exactly: FindAll(string) compares types case-insensitively, so claims typed "SCOPE" would otherwise count.
        HashSet<string> userScopes = new(StringComparer.Ordinal);
        foreach (
            var scopeClaim in context.User.Claims.Where(claim =>
                string.Equals(claim.Type, "scope", StringComparison.Ordinal)
            )
        )
        {
            userScopes.UnionWith(scopeClaim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        if (requirement.AllowedScopes.Exists(userScopes.Contains))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
