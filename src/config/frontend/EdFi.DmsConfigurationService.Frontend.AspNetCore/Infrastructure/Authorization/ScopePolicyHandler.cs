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
        var scopeClaim = context.User.Claims.FirstOrDefault(c => c.Type == "scope");

        if (scopeClaim != null)
        {
            var userScopes = scopeClaim.Value.Split(' ');

            if (requirement.AllowedScopes.Any(scope => userScopes.Contains(scope)))
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}
