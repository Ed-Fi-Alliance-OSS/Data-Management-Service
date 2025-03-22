// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.Authorization;

public class AuthorizationScopePolicies
{
    public static readonly PolicyDefinition AdminScopePolicy = new(
        "AdminScopePolicyPolicy",
        AuthorizationScopes.AdminScope.Name
    );
    public static readonly PolicyDefinition ReadOnlyScopePolicy = new(
        "ReadOnlyScopePolicyPolicy",
        AuthorizationScopes.ReadOnlyScope.Name
    );
    public static readonly PolicyDefinition LimitedAccessScopePolicy = new(
        "LimitedAccessScopePolicy",
        AuthorizationScopes.LimitedAccessScope.Name
    );
    public static readonly IEnumerable<PolicyDefinition> Policies =
    [
        AdminScopePolicy,
        ReadOnlyScopePolicy,
        LimitedAccessScopePolicy,
    ];
}

public record ScopeDefinition(string Name, string Description);

public static class AuthorizationScopes
{
    public static readonly ScopeDefinition AdminScope = new(
        "edfi_admin_api/full_access",
        "Full access to the CMS API endpoints"
    );
    public static readonly ScopeDefinition ReadOnlyScope = new(
        "edfi_admin_api/readonly_access",
        "Read only access"
    );
    public static readonly ScopeDefinition LimitedAccessScope = new(
        "edfi_admin_api/limited_access",
        "Limited access to specific endpoints"
    );

    public static readonly IEnumerable<ScopeDefinition> AllScopes =
    [
        AdminScope,
        ReadOnlyScope,
        LimitedAccessScope,
    ];
}

public record PolicyDefinition(string PolicyName, string Scope);
