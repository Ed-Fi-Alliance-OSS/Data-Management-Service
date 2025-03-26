// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Authorization;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;

/// <summary>
/// Represents an authorization policy that verifies if a user has the required scopes.
/// ScopePolicy is used to enforce scope-based access control
/// by checking the claims in the user's JWT token.
/// </summary>
public class ScopePolicy(params string[] scopes) : IAuthorizationRequirement
{
    public List<string> AllowedScopes { get; } = [.. scopes];
}
