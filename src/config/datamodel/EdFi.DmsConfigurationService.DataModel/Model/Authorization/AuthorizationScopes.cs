// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.Authorization;

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
