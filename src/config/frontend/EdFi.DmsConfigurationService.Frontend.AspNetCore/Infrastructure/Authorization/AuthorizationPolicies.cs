// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;

public class ScopePolicy(params string[] scopes) : IAuthorizationRequirement
{
    public List<string> AllowedScopes { get; } = [.. scopes];
}

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

public static class AuthorizationScopePolicies
{
    public const string AdminScopePolicy = "AdminScopePolicy";
    public const string ReadOnlyOrAdminScopePolicy = "ReadOnlyOrAdminScopePolicy";
    public const string AdminOrAuthorizationEndpointsAccessScopePolicyOrReadOnly =
        "AdminOrAuthorizationEndpointsAccessScopePolicyOrReadOnly";

    public static void Add(AuthorizationOptions options)
    {
        // Admin scope policy (Full Access)
        options.AddPolicy(
            AdminScopePolicy,
            policy => policy.Requirements.Add(new ScopePolicy(AuthorizationScopes.AdminScope.Name))
        );

        // Combined policy (ReadOnly or Admin)
        options.AddPolicy(
            ReadOnlyOrAdminScopePolicy,
            policy =>
                policy.Requirements.Add(
                    new ScopePolicy(
                        AuthorizationScopes.ReadOnlyScope.Name,
                        AuthorizationScopes.AdminScope.Name
                    )
                )
        );

        // Combined policy (Limited to only authorization endpoints or Admin or ReadOnly)
        options.AddPolicy(
            AdminOrAuthorizationEndpointsAccessScopePolicyOrReadOnly,
            policy =>
                policy.Requirements.Add(
                    new ScopePolicy(
                        AuthorizationScopes.AdminScope.Name,
                        AuthorizationScopes.AuthorizationEndpointsAccessScope.Name,
                        AuthorizationScopes.ReadOnlyScope.Name
                    )
                )
        );
    }
}
