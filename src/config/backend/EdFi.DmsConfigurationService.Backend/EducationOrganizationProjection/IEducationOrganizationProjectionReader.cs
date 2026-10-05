// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>
/// Reads the complete education-organization projection of one data store from DMS (DMS-1440 spec §5.1, §5.4).
/// </summary>
public interface IEducationOrganizationProjectionReader
{
    /// <summary>
    /// Every item of the store's projection, from one consistent set, or a <see cref="EducationOrganizationProjectionReadResult.Failure"/>.
    /// A failure never carries items. Only cancellation of <paramref name="cancellationToken"/> throws
    /// (<see cref="OperationCanceledException"/>), with that token; it wins over any timeout that occurs with it.
    /// </summary>
    Task<EducationOrganizationProjectionReadResult> ReadAllAsync(
        EducationOrganizationProjectionReadRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>Which data store to read.</summary>
/// <param name="TenantName">The tenant, or <c>null</c> in single-tenant mode.</param>
/// <param name="DataStoreId">The CMS data store id, sent as <c>dataStoreId</c>.</param>
/// <param name="DataStoreContexts">The store's route contexts (context key to value) from the CMS catalog.</param>
public sealed record EducationOrganizationProjectionReadRequest(
    string? TenantName,
    int DataStoreId,
    IReadOnlyDictionary<string, string> DataStoreContexts
);

/// <summary>The outcome of <see cref="IEducationOrganizationProjectionReader.ReadAllAsync"/>.</summary>
public abstract record EducationOrganizationProjectionReadResult
{
    private EducationOrganizationProjectionReadResult() { }

    /// <summary>
    /// The whole set, in ascending id order, read by the attempt that reached its last page.
    /// </summary>
    /// <param name="Items">Every item of the set.</param>
    /// <param name="ContractVersion">The contract version read.</param>
    /// <param name="PageCount">The pages the successful attempt read.</param>
    /// <param name="Restarts">The attempts restarted before it.</param>
    public sealed record Success(
        IReadOnlyList<EducationOrganizationProjectionItem> Items,
        string ContractVersion,
        int PageCount,
        int Restarts
    ) : EducationOrganizationProjectionReadResult;

    public sealed record Failure(EducationOrganizationProjectionFailure Detail)
        : EducationOrganizationProjectionReadResult;
}

/// <summary>One education organization of the projection, as DMS serves it.</summary>
public sealed record EducationOrganizationProjectionItem(
    long EducationOrganizationId,
    string NameOfInstitution,
    string? ShortNameOfInstitution,
    string Discriminator,
    long? ParentId
);
