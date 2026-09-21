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
    /// per-provider branching. Treat any change here as a breaking change to that check.
    /// </summary>
    public const string ClientIdClaimType = "client_id";
}
