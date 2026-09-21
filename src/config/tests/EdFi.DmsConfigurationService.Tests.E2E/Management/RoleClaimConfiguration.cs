// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Tests.E2E.Management;

/// <summary>
/// The client role and role-claim type the running stack was configured with. The two identity
/// providers read the claim type from different settings, so the expectation is derived from the
/// provider under test rather than assumed to be one shared value.
/// </summary>
/// <remarks>
/// <c>build-config.ps1</c> publishes these values by reading them back from the Configuration
/// Service container, so they are what the running service actually received rather than what an
/// environment file requested. The fallbacks below match the checked-in environment files, so a
/// bare <c>dotnet test</c> against a standard stack still compares against the right values.
/// </remarks>
public static class RoleClaimConfiguration
{
    private const string IdentityProviderVariable = "DMS_CONFIG_IDENTITY_PROVIDER";

    /// <summary>
    /// Keycloak emits the claim named by <c>IdentitySettings:RoleClaimType</c>.
    /// </summary>
    private const string KeycloakClaimTypeVariable = "DMS_CONFIG_IDENTITY_ROLE_CLAIM_TYPE";

    /// <summary>
    /// The self-contained provider emits the claim named by
    /// <c>Authentication:RoleClaimAttribute</c>, falling back to the same URI Keycloak is
    /// configured with. No compose file forwards that key to the container, so the two lanes agree
    /// today by default rather than by configuration; reading each provider's own setting keeps a
    /// customized lane honest instead of comparing against the other provider's value.
    /// </summary>
    private const string SelfContainedClaimTypeVariable = "Authentication__RoleClaimAttribute";

    private const string DefaultRoleClaimType =
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

    private const string ClientRoleVariable = "DMS_CONFIG_IDENTITY_CLIENT_ROLE";

    private const string DefaultClientRole = "dms-client";

    private static string IdentityProvider => EnvOrDefault(IdentityProviderVariable, "keycloak");

    private static bool IsSelfContained =>
        string.Equals(IdentityProvider, "self-contained", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The claim type the provider under test emits its roles under.
    /// </summary>
    public static string ExpectedRoleClaimType() =>
        IsSelfContained
            ? EnvOrDefault(SelfContainedClaimTypeVariable, DefaultRoleClaimType)
            : EnvOrDefault(KeycloakClaimTypeVariable, DefaultRoleClaimType);

    /// <summary>
    /// The realm role every client created through the Configuration Service receives.
    /// </summary>
    public static string ExpectedClientRole() => EnvOrDefault(ClientRoleVariable, DefaultClientRole);

    /// <summary>
    /// The effective configuration, for a failure message that says which lane was under test.
    /// </summary>
    public static string Describe() =>
        $"identity provider '{IdentityProvider}', claim type '{ExpectedRoleClaimType()}', "
        + $"client role '{ExpectedClientRole()}'";

    private static string EnvOrDefault(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}
