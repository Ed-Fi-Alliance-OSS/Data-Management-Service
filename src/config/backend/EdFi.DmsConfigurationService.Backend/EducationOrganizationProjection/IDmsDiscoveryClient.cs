// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>
/// Locates the projection for one data store through DMS Discovery (DMS-1440 spec §5.3): reads and caches the tenant's
/// Discovery document, chooses the contract version, and resolves both URL templates for the store.
/// </summary>
public interface IDmsDiscoveryClient
{
    /// <summary>
    /// The token and projection URLs and the contract version for a data store, or a <see cref="DmsDiscoveryResolution.Failed"/>
    /// at the Discovery stage. A Discovery request is bounded by <c>DiscoveryTimeoutSeconds</c> and by
    /// <paramref name="readDeadline"/>; reaching either is a transient <c>Timeout</c>. Only cancellation of
    /// <paramref name="cancellationToken"/> throws (<see cref="OperationCanceledException"/>).
    /// </summary>
    /// <param name="tenantName">The tenant, or <c>null</c> in single-tenant mode.</param>
    /// <param name="dataStoreContexts">The store's route contexts (context key to value) that fill placeholders.</param>
    /// <param name="readDeadline">When the whole logical read times out.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    Task<DmsDiscoveryResolution> ResolveAsync(
        string? tenantName,
        IReadOnlyDictionary<string, string> dataStoreContexts,
        DateTimeOffset readDeadline,
        CancellationToken cancellationToken
    );

    /// <summary>Drops the cached Discovery document of a tenant (<c>null</c> in single-tenant mode).</summary>
    void Invalidate(string? tenantName);
}

/// <summary>The outcome of <see cref="IDmsDiscoveryClient.ResolveAsync"/>.</summary>
public abstract record DmsDiscoveryResolution
{
    private DmsDiscoveryResolution() { }

    /// <summary>Both URLs passed containment and every placeholder was filled.</summary>
    public sealed record Resolved(Uri TokenUrl, Uri ProjectionUrl, string ContractVersion)
        : DmsDiscoveryResolution;

    public sealed record Failed(EducationOrganizationProjectionFailure Failure) : DmsDiscoveryResolution;
}
