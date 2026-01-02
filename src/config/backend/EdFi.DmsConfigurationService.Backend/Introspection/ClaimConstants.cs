// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Introspection;

/// <summary>
/// Defines claim URI constants used by the DMS Configuration Service for authorization and token introspection.
/// Aligned with Ed-Fi ODS API standards for compatibility.
/// </summary>
public static class ClaimConstants
{
    /// <summary>
    /// The base prefix for Ed-Fi ODS identity claims.
    /// Example: "http://ed-fi.org/identity/claims/ed-fi/students"
    /// </summary>
    public const string OdsIdentityClaimsPrefix = "http://ed-fi.org/identity/claims/";

    /// <summary>
    /// The base prefix for Ed-Fi identity claims (alternate format without "ods").
    /// Example: "http://ed-fi.org/identity/claims/ed-fi/academicWeek"
    /// </summary>
    public const string IdentityClaimsPrefix = "http://ed-fi.org/identity/claims/";

    /// <summary>
    /// The prefix for service-level claims (as opposed to data resource claims).
    /// Service claims identify access to functional endpoints like identity or rostering services.
    /// Example: "http://ed-fi.org/identity/claims/services/identity"
    /// </summary>
    public const string ServicesPrefix = "http://ed-fi.org/identity/claims/services/";

    /// <summary>
    /// The prefix for domain-level resource claims.
    /// Example: "http://ed-fi.org/identity/claims/domains/edFiDescriptors"
    /// </summary>
    public const string DomainsPrefix = "domains/";
}
