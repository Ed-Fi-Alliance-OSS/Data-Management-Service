// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>
/// The <c>DmsEducationOrganizationProjectionSettings</c> section (DMS-1440 spec §5.2): where the Configuration Service
/// reads the DMS education-organization projection and with which credentials.
/// <see cref="DmsEducationOrganizationProjectionSettingsValidator"/> checks it at startup when
/// <see cref="DmsBaseUrl"/> is set; with no base URL every read is <c>NotConfigured</c>.
/// </summary>
public sealed class DmsEducationOrganizationProjectionSettings
{
    public const string SectionName = "DmsEducationOrganizationProjectionSettings";

    /// <summary>The contract versions this Configuration Service can parse.</summary>
    public static IReadOnlyList<string> SupportedContractVersions { get; } =
    ["educationOrganizationProjection.v1"];

    /// <summary>
    /// The DMS root that Discovery is read from: an absolute http or https URL, possibly with a base path, with no
    /// query or fragment. Unset means the reader is not configured.
    /// </summary>
    public string? DmsBaseUrl { get; set; }

    /// <summary>The single-tenant credential, and the fallback for a tenant with no entry of its own.</summary>
    public DmsEducationOrganizationProjectionCredentials? Credentials { get; set; }

    /// <summary>Per-tenant credentials, keyed by tenant name in any letter case.</summary>
    public Dictionary<string, DmsEducationOrganizationProjectionCredentials> TenantCredentials { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The configured contract versions, or <c>null</c> for <see cref="SupportedContractVersions"/>. It has no
    /// initializer because the configuration binder appends to an initialized collection instead of replacing it.
    /// </summary>
    public string[]? ContractVersions { get; set; }

    /// <summary>The versions this reader offers: the configured ones, or every supported version.</summary>
    public IReadOnlyList<string> EffectiveContractVersions =>
        ContractVersions is { Length: > 0 } ? ContractVersions : SupportedContractVersions;

    /// <summary>Items requested per page, sent as <c>limit</c>.</summary>
    public int PageSize { get; set; } = 2000;

    public int DiscoveryTimeoutSeconds { get; set; } = 10;

    public int TokenRequestTimeoutSeconds { get; set; } = 30;

    public int PageRequestTimeoutSeconds { get; set; } = 60;

    /// <summary>The whole logical read, including Discovery, token requests and restarts.</summary>
    public int TotalReadTimeoutSeconds { get; set; } = 600;

    /// <summary>Pages one attempt may read.</summary>
    public int MaxPages { get; set; } = 2000;

    /// <summary>Items one attempt may collect.</summary>
    public int MaxItems { get; set; } = 500_000;

    /// <summary>The largest response body read, in bytes.</summary>
    public int MaxResponseBodyBytes { get; set; } = 8_388_608;

    /// <summary>Restarts after <c>projection-changed</c> or a stale cursor before the read fails.</summary>
    public int MaxWalkRestarts { get; set; } = 3;

    /// <summary>How long before its expiry a cached token is no longer used.</summary>
    public int TokenExpirySafetyMarginSeconds { get; set; } = 60;

    /// <summary>How long a Discovery document is cached per tenant.</summary>
    public int DiscoveryCacheSeconds { get; set; } = 300;
}

/// <summary>
/// A client-credentials pair for the projection. <see cref="ToString"/> never includes either value.
/// </summary>
public sealed class DmsEducationOrganizationProjectionCredentials
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public override string ToString() =>
        $"{nameof(DmsEducationOrganizationProjectionCredentials)} {{ {nameof(ClientId)} = [redacted], {nameof(ClientSecret)} = [redacted] }}";
}
