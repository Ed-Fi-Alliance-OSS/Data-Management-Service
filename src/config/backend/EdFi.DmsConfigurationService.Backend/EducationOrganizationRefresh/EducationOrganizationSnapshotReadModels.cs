// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.Jobs;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;

/// <summary>An ordinary data store to refresh and its contexts, keyed by context key with ordinal comparison.</summary>
public sealed record EducationOrganizationRefreshTarget(
    int DataStoreId,
    IReadOnlyDictionary<string, string> Contexts
);

/// <summary>
/// One entry of the tenant aggregate: an ordinary data store with the organizations of its published snapshot, or a
/// management record linked to no data store, which has no <paramref name="Id"/> and no organizations.
/// </summary>
/// <param name="Id">The ordinary data store, or null for a management record linked to none.</param>
/// <param name="DataStoreManageId">The linked or unlinked management record, or null for an unmanaged store.</param>
/// <param name="Name">The ordinary data store's name, or the management record's when it is linked to none.</param>
/// <param name="DataStoreType">The ordinary data store's type, or null for a management record linked to none.</param>
/// <param name="Status">The management record's status, or <c>Created</c> for an unmanaged store.</param>
/// <param name="DatabaseTemplate">The management record's template, or null for an unmanaged store.</param>
/// <param name="DatabaseName">
/// The management record's target database name once it was published, otherwise null, as the management API reports it.
/// </param>
/// <param name="EducationOrganizations">The published snapshot ordered by identifier; empty when there is none.</param>
public sealed record TenantAggregateDataStore(
    int? Id,
    int? DataStoreManageId,
    string Name,
    string? DataStoreType,
    string Status,
    string? DatabaseTemplate,
    string? DatabaseName,
    IReadOnlyList<EducationOrganizationProjectionItem> EducationOrganizations
);

/// <summary>The outcome of <see cref="IEducationOrganizationSnapshotRepository.GetRefreshTargetsAsync"/>.</summary>
public abstract record SnapshotRefreshTargetsResult
{
    private SnapshotRefreshTargetsResult() { }

    /// <param name="Targets">The current tenant's matching data stores, ordered by identifier.</param>
    public sealed record Success(IReadOnlyList<EducationOrganizationRefreshTarget> Targets)
        : SnapshotRefreshTargetsResult;

    public sealed record DbFailure(JobFailureDiagnostic Diagnostic) : SnapshotRefreshTargetsResult;
}

/// <summary>The outcome of <see cref="IEducationOrganizationSnapshotRepository.DataStoreExistsAsync"/>.</summary>
public abstract record SnapshotDataStoreExistsResult
{
    private SnapshotDataStoreExistsResult() { }

    public sealed record Found : SnapshotDataStoreExistsResult;

    /// <summary>The data store does not exist in the current tenant.</summary>
    public sealed record NotFound : SnapshotDataStoreExistsResult;

    public sealed record DbFailure(JobFailureDiagnostic Diagnostic) : SnapshotDataStoreExistsResult;
}

/// <summary>The outcome of <see cref="IEducationOrganizationSnapshotRepository.GetTenantAggregateAsync"/>.</summary>
public abstract record SnapshotTenantAggregateResult
{
    private SnapshotTenantAggregateResult() { }

    /// <param name="DataStores">
    /// The current tenant's ordinary data stores ordered by identifier, followed by its management records linked
    /// to no data store, ordered by identifier.
    /// </param>
    public sealed record Success(IReadOnlyList<TenantAggregateDataStore> DataStores)
        : SnapshotTenantAggregateResult;

    public sealed record DbFailure(JobFailureDiagnostic Diagnostic) : SnapshotTenantAggregateResult;
}

/// <summary>
/// Statement (a) of the aggregate read: an ordinary data store, its linked management record, if any, and the active
/// generation of its target, if any.
/// </summary>
public sealed record TenantAggregateDataStoreRow(
    int Id,
    string Name,
    string DataStoreType,
    int? DataStoreManageId,
    string? Status,
    string? Template,
    string? TargetDatabaseName,
    int? HistoricalDataStoreId,
    long? ActiveGeneration
);

/// <summary>Statement (b) of the aggregate read: an organization of an active generation.</summary>
public sealed record TenantAggregateOrganizationRow(
    int DataStoreId,
    long EducationOrganizationId,
    string NameOfInstitution,
    string? ShortNameOfInstitution,
    string Discriminator,
    long? ParentId
);

/// <summary>Statement (c) of the aggregate read: a management record linked to no data store.</summary>
public sealed record TenantAggregateUnlinkedManageRow(
    int Id,
    string Name,
    string Status,
    string Template,
    string TargetDatabaseName,
    int? HistoricalDataStoreId
);

/// <summary>Builds the tenant aggregate from the rows of its three statements, the same way for every provider.</summary>
public static class TenantAggregateAssembler
{
    /// <summary>The status reported for an ordinary data store that no management record links to.</summary>
    public const string UnmanagedStatus = "Created";

    /// <summary>
    /// Keeps the order of <paramref name="dataStores"/> and appends <paramref name="unlinked"/> in their order. Each
    /// store takes the rows of <paramref name="organizations"/> with its identifier, in their order.
    /// </summary>
    public static IReadOnlyList<TenantAggregateDataStore> Assemble(
        IReadOnlyList<TenantAggregateDataStoreRow> dataStores,
        IReadOnlyList<TenantAggregateOrganizationRow> organizations,
        IReadOnlyList<TenantAggregateUnlinkedManageRow> unlinked
    )
    {
        ArgumentNullException.ThrowIfNull(dataStores);
        ArgumentNullException.ThrowIfNull(organizations);
        ArgumentNullException.ThrowIfNull(unlinked);

        ILookup<int, EducationOrganizationProjectionItem> byDataStore = organizations.ToLookup(
            row => row.DataStoreId,
            row => new EducationOrganizationProjectionItem(
                row.EducationOrganizationId,
                row.NameOfInstitution,
                row.ShortNameOfInstitution,
                row.Discriminator,
                row.ParentId
            )
        );

        List<TenantAggregateDataStore> aggregate = new(dataStores.Count + unlinked.Count);
        aggregate.AddRange(
            dataStores.Select(row => new TenantAggregateDataStore(
                row.Id,
                row.DataStoreManageId,
                row.Name,
                row.DataStoreType,
                row.DataStoreManageId is null ? UnmanagedStatus : row.Status!,
                row.DataStoreManageId is null ? null : row.Template,
                row.DataStoreManageId is null
                    ? null
                    : DatabaseName(row.HistoricalDataStoreId, row.TargetDatabaseName),
                [.. byDataStore[row.Id]]
            ))
        );
        aggregate.AddRange(
            unlinked.Select(row => new TenantAggregateDataStore(
                null,
                row.Id,
                row.Name,
                null,
                row.Status,
                row.Template,
                DatabaseName(row.HistoricalDataStoreId, row.TargetDatabaseName),
                []
            ))
        );
        return aggregate;
    }

    // The management API reports the target database name only once the record was published.
    private static string? DatabaseName(int? historicalDataStoreId, string? targetDatabaseName) =>
        historicalDataStoreId is null ? null : targetDatabaseName;
}
