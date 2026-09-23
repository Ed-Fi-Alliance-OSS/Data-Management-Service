// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel;

public static class SecurityConstants
{
    public const string ServicePolicy = "ServicePolicy";

    /// <summary>
    /// Claim type naming the OAuth client a token was issued to. Self-contained (OpenIddict) and
    /// Keycloak-issued tokens both use this name, so the revocation ownership check needs no
    /// per-provider branching.
    ///
    /// Changing this value is a breaking change, and the compiler will not show you how far it
    /// reaches. Within CMS it covers the claim minted by <c>JwtTokenGenerator</c>, the revocation
    /// ownership check, <c>GetClientId()</c> and <c>AuditContext</c> — but treat that as examples,
    /// not an exhaustive list.
    ///
    /// Three categories of reader are outside this constant entirely:
    /// <list type="bullet">
    /// <item>DMS (<c>src/dms</c>) reads the same claim name through its own hardcoded literals —
    /// <c>Security/JwtValidationService.cs</c> and <c>Security/ApiClientDetailsProvider.cs</c> —
    /// and has no reference to this assembly, the two being separate products. Renaming the claim
    /// here is therefore a cross-product breaking change that compiles cleanly and fails at
    /// runtime.</item>
    /// <item>Keycloak-issued tokens, whose claim name Keycloak controls, so a rename here would
    /// break the ownership comparison in Keycloak mode.</item>
    /// <item>The <c>client_id</c> OAuth *request parameter* read from the form body in
    /// <c>GetAccessTokenAsync</c>, fixed by RFC 6749 and kept a separate literal on purpose so
    /// that renaming the claim cannot rename the wire parameter.</item>
    /// </list>
    /// </summary>
    public const string ClientIdClaimType = "client_id";
}
