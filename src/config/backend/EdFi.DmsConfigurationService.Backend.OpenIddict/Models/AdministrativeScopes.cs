// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Authorization;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

/// <summary>
/// The Configuration Service's recognized Management API scopes, as a test over a client's
/// registered scopes.
/// </summary>
public static class AdministrativeScopes
{
    /// <summary>
    /// True when <paramref name="scopes"/> is nonempty and every entry equals, ordinally, one of
    /// <see cref="AuthorizationScopes.AllScopes"/>.
    /// </summary>
    public static bool AllRecognized(IReadOnlyCollection<string> scopes) =>
        scopes.Count > 0
        && scopes.All(scope =>
            AuthorizationScopes.AllScopes.Any(adminScope =>
                string.Equals(scope, adminScope.Name, StringComparison.Ordinal)
            )
        );
}
