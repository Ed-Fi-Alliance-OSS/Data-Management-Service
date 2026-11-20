// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.Jobs;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;

/// <summary>
/// The education organization snapshot of each ordinary data store and the lease that coordinates its refreshes.
/// </summary>
/// <remarks>
/// <para>
/// A refresh acquires the data store's target lease, which allocates a new generation, removes what earlier
/// executions left unpublished, stages its organizations under that generation, and publishes the generation by
/// activating it inside the job fence. Only the live lease holder can reclaim, stage or activate, so publication
/// follows acquisition order and a stale execution can never overwrite a newer snapshot.
/// </para>
/// <para>
/// Every operation whose decision depends on the lease locks the target row in its own statement, reads the
/// database clock together with the locked row in a separate statement, and only then writes, using that clock
/// value, all in one transaction. A lease that expires while the operation waits for the row lock is therefore
/// never honored. Lease times come from the database clock only, and the lease lasts
/// <c>JobSettings:LeaseDuration</c>. A renewal waits for the row lock at most within
/// <c>JobSettings:RenewalInterval / 2</c>; the other operations wait at most 5 seconds.
/// </para>
/// <para>
/// Every operation is confined to the targets of the current tenant, read once per call: a target of another
/// partition behaves as absent, and nothing of it is read or changed. This keeps partitions apart; it is not an
/// authorization boundary.
/// </para>
/// <para>
/// A database failure is returned as <c>DbFailure</c> with a diagnostic that carries no message or value, except in
/// <see cref="ActivateAsync"/>, which throws <see cref="SnapshotStoreException"/> so that the fence rolls back.
/// Cancellation of the caller's token propagates as <see cref="OperationCanceledException"/>.
/// </para>
/// </remarks>
public interface IEducationOrganizationSnapshotRepository
{
    /// <summary>
    /// The current tenant's ordinary data stores with their contexts, ordered by identifier: all of them, or only
    /// <paramref name="dataStoreId"/> when it is given. Connection strings are never read, so a data store whose
    /// connection string cannot be decrypted is still listed.
    /// </summary>
    Task<SnapshotRefreshTargetsResult> GetRefreshTargetsAsync(
        int? dataStoreId,
        CancellationToken cancellationToken
    );

    /// <summary>Whether the ordinary data store exists in the current tenant.</summary>
    Task<SnapshotDataStoreExistsResult> DataStoreExistsAsync(
        int dataStoreId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// The current tenant's ordinary data stores, each with its linked management record and the organizations of
    /// its published snapshot, followed by the tenant's management records linked to no data store. The whole result
    /// is read at one consistent point: a data store, management or publication change that commits concurrently is
    /// either wholly included or wholly absent.
    /// </summary>
    Task<SnapshotTenantAggregateResult> GetTenantAggregateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Takes the lease of the data store's target for <paramref name="leaseOwner"/> and allocates the next
    /// generation, creating the target on its first refresh. The data store must exist in the current tenant.
    /// A live lease held by any execution makes the result <see cref="SnapshotAcquireResult.Busy"/>, with nothing
    /// written; an expired lease is taken over.
    /// </summary>
    Task<SnapshotAcquireResult> AcquireAsync(
        int dataStoreId,
        string leaseOwner,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Under the live lease, deletes every organization row that belongs neither to the lease's generation nor to
    /// the active generation. Success reports the rows deleted.
    /// </summary>
    Task<SnapshotTargetResult> ReclaimAsync(
        EducationOrganizationSnapshotLease lease,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Extends the live lease by the lease duration from the current database time. An expired or taken lease is
    /// never revived.
    /// </summary>
    Task<SnapshotTargetResult> RenewAsync(
        EducationOrganizationSnapshotLease lease,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Under the live lease, inserts <paramref name="items"/> as the organizations of the lease's generation. The
    /// target row stays share-locked until the insert commits, so no other execution can take the lease over
    /// meanwhile. Success reports the rows inserted; an empty list inserts nothing.
    /// </summary>
    Task<SnapshotTargetResult> StageAsync(
        EducationOrganizationSnapshotLease lease,
        IReadOnlyList<EducationOrganizationProjectionItem> items,
        string sourceContractVersion,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// In <paramref name="transaction"/>, the job fence's, publishes the lease's generation as the active one,
    /// records the refresh time and contract version, and clears the lease. Nothing is written unless the lease is
    /// live. A transaction of another provider, or one that has completed or been disposed, is rejected before any
    /// statement runs, so the activation can never run outside it. A database failure throws
    /// <see cref="SnapshotStoreException"/>.
    /// </summary>
    Task<SnapshotTargetResult> ActivateAsync(
        DbTransaction transaction,
        EducationOrganizationSnapshotLease lease,
        string sourceContractVersion,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes the organization rows of generations older than the active one. It needs no lease. Success reports
    /// the rows deleted.
    /// </summary>
    Task<SnapshotTargetResult> PruneAsync(int dataStoreId, CancellationToken cancellationToken);

    /// <summary>
    /// Clears the lease when <paramref name="lease"/> still holds it, live or expired. Success reports whether a
    /// lease was cleared (1) or there was nothing to clear (0).
    /// </summary>
    Task<SnapshotTargetResult> ReleaseAsync(
        EducationOrganizationSnapshotLease lease,
        CancellationToken cancellationToken
    );
}

/// <summary>A target lease: the execution that holds it and the generation it may stage and publish.</summary>
/// <param name="DataStoreId">The ordinary data store whose snapshot the lease covers.</param>
/// <param name="Owner">The execution token, a <c>Guid</c> in <c>N</c> format.</param>
/// <param name="Generation">The generation allocated by the acquisition.</param>
public sealed record EducationOrganizationSnapshotLease(int DataStoreId, string Owner, long Generation);

/// <summary>Why a lease no longer authorizes its holder.</summary>
public enum SnapshotLeaseLossReason
{
    /// <summary>The lease is still recorded for this holder, but it expired by database time.</summary>
    Expired,

    /// <summary>Another execution holds the lease, or the lease was released or published.</summary>
    Taken,
}

/// <summary>The outcome of <see cref="IEducationOrganizationSnapshotRepository.AcquireAsync"/>.</summary>
public abstract record SnapshotAcquireResult
{
    private SnapshotAcquireResult() { }

    public sealed record Acquired(EducationOrganizationSnapshotLease Lease) : SnapshotAcquireResult;

    /// <summary>Another execution holds a live lease; nothing was written.</summary>
    public sealed record Busy : SnapshotAcquireResult;

    /// <summary>The data store does not exist in the current tenant.</summary>
    public sealed record DataStoreNotFound : SnapshotAcquireResult;

    public sealed record DbFailure(JobFailureDiagnostic Diagnostic) : SnapshotAcquireResult;
}

/// <summary>The outcome of an operation on an acquired target.</summary>
public abstract record SnapshotTargetResult
{
    private SnapshotTargetResult() { }

    /// <param name="Rows">The rows the operation inserted, deleted or cleared, as each operation documents.</param>
    public sealed record Success(int Rows) : SnapshotTargetResult;

    /// <summary>The lease does not authorize the caller; nothing was written.</summary>
    public sealed record LeaseLost(SnapshotLeaseLossReason Reason) : SnapshotTargetResult;

    /// <summary>
    /// The current tenant has no such target: its data store was deleted, or belongs to another partition.
    /// </summary>
    public sealed record DataStoreRemoved : SnapshotTargetResult;

    public sealed record DbFailure(JobFailureDiagnostic Diagnostic) : SnapshotTargetResult;
}

/// <summary>
/// A database failure inside a caller's transaction, such as the job fence's, carrying only a diagnostic without
/// any message or value.
/// </summary>
public sealed class SnapshotStoreException(JobFailureDiagnostic diagnostic)
    : Exception("The education organization snapshot store failed.")
{
    public JobFailureDiagnostic Diagnostic { get; } = diagnostic;
}
