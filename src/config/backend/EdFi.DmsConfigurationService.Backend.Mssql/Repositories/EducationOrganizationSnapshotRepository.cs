// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using EdFi.DmsConfigurationService.Backend.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

/// <summary>
/// SQL Server education organization snapshots and their target leases.
/// </summary>
/// <remarks>
/// <para>
/// Each operation is one transaction. A lease decision locks the target row first (<c>UPDLOCK, HOLDLOCK</c>, or
/// <c>HOLDLOCK</c> alone while staging), then reads <c>SYSUTCDATETIME()</c> with the locked row in a separate
/// statement, because SQL Server reads <c>SYSUTCDATETIME()</c> once when a statement starts, so a statement that
/// waited for the lock would judge the lease by time read before the wait. The write then uses that clock value,
/// passed back as <c>datetime2</c>. Acquisition first takes a shared lock, held to commit, on the data store row,
/// so a concurrent delete either finishes first, and the data store is not found, or waits until the acquisition
/// commits and then cascades. The target lock's key-range part covers a target that does not exist yet, so two
/// first acquisitions serialize: the second waits, then finds the first one's live lease.
/// </para>
/// <para>
/// A staging transaction holds a shared lock on the target row. An acquisition's update lock is compatible with it,
/// so a takeover can decide while staging runs, but its write needs an exclusive lock and waits until the staging
/// commits; the new owner then removes those rows when it reclaims.
/// </para>
/// <para>
/// Each operation reads the tenant context once and filters every statement it issues, lookups and writes alike,
/// by that tenant's <c>TenantId</c>, so a target of another partition behaves as absent. This keeps partitions
/// apart; it is not an authorization boundary.
/// </para>
/// <para>
/// Every wait runs through a <see cref="JobDatabaseSession"/> under a deadline. A renewal's deadline is
/// <c>JobSettings:RenewalInterval / 2</c> and its <c>LOCK_TIMEOUT</c> one second shorter, so a lock wait normally
/// ends in the lock-timeout error (<c>1222</c>); when the deadline ends the wait first, the session is handed over
/// and the operation fails in the same way, with a <see cref="TimeoutException"/> diagnostic. Release has 5 seconds,
/// with a 4-second lock wait. Every other operation has the lease duration, with a 5-second lock wait.
/// <c>LOCK_TIMEOUT</c> is session-scoped, so every session is ended, within its deadline, by rolling back any open
/// transaction and resetting it to <c>-1</c>. Activation runs in the job fence's transaction, under the fence's
/// lock wait and deadline.
/// </para>
/// <para>
/// Organization rows are inserted with ordinary <c>INSERT … WITH (ROWLOCK)</c> statements that read their rows
/// from a JSON parameter, so the foreign key and check constraints are enforced and stay trusted; reclaim and
/// prune delete <c>WITH (ROWLOCK)</c> rows whose keys a <c>FORCESEEK</c> subquery chooses within the data store's
/// key range, joined to the target table on the full primary key under <c>OPTION (LOOP JOIN)</c>. A scan plan,
/// which the optimizer chose for a plain batched delete, reads past other data stores' rows and waits for any that
/// another transaction holds, and SQL Server refuses <c>FORCESEEK</c> on a delete's target. The statements that
/// lock or read a target or data store row seek for the same reason. A bulk copy is not used: without a table lock
/// it loads by page and holds exclusive page locks, which block other data stores whose rows share those pages.
/// <c>ROWLOCK</c> sets only the initial lock granularity; SQL Server can still escalate to a table lock, for
/// example under lock-memory pressure. Inserts and deletes therefore run in statements of at most
/// <see cref="BatchSize"/> rows, all in the operation's transaction, below the 5,000-lock threshold at which one
/// statement escalates, which reduces that risk without ruling it out. An operation of another data store that
/// meets such a lock waits at most its lock wait and fails like any other lock wait.
/// </para>
/// <para>
/// The reads run in ordinary <c>READ COMMITTED</c> transactions under the 5-second lock wait. The tenant aggregate
/// first takes the tenant's catalog gate in shared mode. Every writer of ordinary data stores and management links
/// takes that gate exclusively, so none of them can commit until the read ends: a writer that arrives during the
/// read waits at most 5 seconds for the gate, then fails and rolls back. The data stores statement (a) then reads
/// each data store's target row under a shared row lock held to commit (<c>HOLDLOCK</c>); an activation's update
/// lock is compatible with it, but the update itself needs an exclusive lock and waits until the read ends, so
/// the active generations that (a) found stay active. The organizations statement (b) seeks only the rows of those
/// generations, which staging, reclaim and prune never change, and the unlinked management records statement (c)
/// reads under the gate. The whole result is therefore the state at one point: a change either committed before
/// the gate and target locks were taken, or waits for the read. While it runs, the read also delays any
/// acquisition, takeover or renewal that must update a target row it locked, and a first acquisition whose new
/// target row falls in a key range it locked for a missing target, each by at most that operation's lock wait.
/// </para>
/// </remarks>
public sealed class EducationOrganizationSnapshotRepository(
    IOptions<DatabaseOptions> databaseOptions,
    IOptions<JobOptions> jobOptions,
    ITenantContextProvider tenantContextProvider
) : IEducationOrganizationSnapshotRepository
{
    internal const int BatchSize = 4_000;

    private const int LeaseOwnerMaxLength = 32;
    private const string UpdateLock = "UPDLOCK, HOLDLOCK, ROWLOCK, FORCESEEK";
    private const string ShareLock = "HOLDLOCK, ROWLOCK, FORCESEEK";

    private static readonly TimeSpan _lockWait = JobLeaseTimings.WriteLockWait;
    private static readonly TimeSpan _releaseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _deadlineHeadroom = TimeSpan.FromSeconds(1);

    private const string ReadClockSql = "SELECT SYSUTCDATETIME();";

    // The data store was found in the current tenant under a shared lock, so the new target takes its tenant.
    private const string InsertTargetSql = """
        INSERT INTO dmscs.EducationOrganizationSnapshotTarget
            (DataStoreId, TenantId, LastGeneration, LeaseOwner, LeaseGeneration, LeaseExpiresAt, LastModifiedAt)
        VALUES (@DataStoreId, @TenantId, 1, @Owner, 1, DATEADD(millisecond, @LeaseMilliseconds, @Now), @Now);
        """;

    // The text columns are read as nvarchar(max), so a value longer than its column fails the insert instead of
    // being cut short.
    private const string InsertOrganizationsSql = """
        INSERT INTO dmscs.EducationOrganizationSnapshot WITH (ROWLOCK)
            (DataStoreId, Generation, TenantId, EducationOrganizationId, NameOfInstitution, ShortNameOfInstitution,
            Discriminator, ParentId, RefreshedAt, SourceContractVersion)
        SELECT @DataStoreId, @Generation, @TenantId, item.Id, item.Name, item.ShortName, item.Discriminator,
            item.ParentId, @Now, @SourceContractVersion
        FROM OPENJSON(@Items)
            WITH (
                Id BIGINT '$.Id',
                Name NVARCHAR(MAX) '$.Name',
                ShortName NVARCHAR(MAX) '$.ShortName',
                Discriminator NVARCHAR(MAX) '$.Discriminator',
                ParentId BIGINT '$.ParentId'
            ) AS item;
        """;

    /// <summary>Test seam: hooks into every session this repository opens.</summary>
    internal JobDatabaseSessionHooks? SessionHooks { get; init; }

    private TimeSpan LeaseDuration => jobOptions.Value.LeaseDuration;

    private TimeSpan RenewalTimeout => jobOptions.Value.RenewalTimeout;

    private int LeaseMilliseconds => (int)LeaseDuration.TotalMilliseconds;

    public Task<SnapshotRefreshTargetsResult> GetRefreshTargetsAsync(
        int? dataStoreId,
        CancellationToken cancellationToken
    )
    {
        TenantScope tenant = CurrentTenant();
        return InTransactionAsync<SnapshotRefreshTargetsResult>(
            "GetRefreshTargets",
            LeaseDuration,
            _lockWait,
            async (session, deadline) =>
            {
                IEnumerable<TargetContextRow> rows = await QueryAsync<TargetContextRow>(
                    session,
                    "ReadTargets",
                    RefreshTargetsSql(tenant),
                    new { DataStoreId = dataStoreId, tenant.TenantId },
                    deadline,
                    cancellationToken
                );
                return Outcome.Commit<SnapshotRefreshTargetsResult>(
                    new SnapshotRefreshTargetsResult.Success(RefreshTargets(rows))
                );
            },
            diagnostic => new SnapshotRefreshTargetsResult.DbFailure(diagnostic),
            cancellationToken
        );
    }

    public Task<SnapshotDataStoreExistsResult> DataStoreExistsAsync(
        int dataStoreId,
        CancellationToken cancellationToken
    )
    {
        TenantScope tenant = CurrentTenant();
        return InTransactionAsync<SnapshotDataStoreExistsResult>(
            "DataStoreExists",
            LeaseDuration,
            _lockWait,
            async (session, deadline) =>
                Outcome.Commit<SnapshotDataStoreExistsResult>(
                    await ScalarAsync<bool>(
                        session,
                        "ReadDataStore",
                        DataStoreExistsSql(tenant),
                        new { DataStoreId = dataStoreId, tenant.TenantId },
                        deadline,
                        cancellationToken
                    )
                        ? new SnapshotDataStoreExistsResult.Found()
                        : new SnapshotDataStoreExistsResult.NotFound()
                ),
            diagnostic => new SnapshotDataStoreExistsResult.DbFailure(diagnostic),
            cancellationToken
        );
    }

    public Task<SnapshotTenantAggregateResult> GetTenantAggregateAsync(CancellationToken cancellationToken)
    {
        TenantScope tenant = CurrentTenant();
        return InTransactionAsync<SnapshotTenantAggregateResult>(
            "GetTenantAggregate",
            LeaseDuration,
            _lockWait,
            async (session, deadline) =>
            {
                await session.RunAsync(
                    "AcquireTenantGate",
                    token =>
                        DataStoreCatalog.AcquireSharedTenantGateAsync(
                            session.Transaction!,
                            tenant.TenantId,
                            token
                        ),
                    deadline,
                    cancellationToken
                );
                List<TenantAggregateDataStoreRow> dataStores = (
                    await QueryAsync<TenantAggregateDataStoreRow>(
                        session,
                        "ReadDataStores",
                        AggregateDataStoresSql(tenant),
                        new { tenant.TenantId },
                        deadline,
                        cancellationToken
                    )
                ).AsList();
                DynamicParameters organizationParameters = new(new { tenant.TenantId });
                organizationParameters.Add(
                    "Active",
                    JsonSerializer.Serialize(
                        dataStores
                            .Where(row => row.ActiveGeneration is not null)
                            .Select(row => new ActiveGeneration(row.Id, row.ActiveGeneration!.Value))
                    ),
                    DbType.String,
                    size: -1
                );
                List<TenantAggregateOrganizationRow> organizations = (
                    await QueryAsync<TenantAggregateOrganizationRow>(
                        session,
                        "ReadOrganizations",
                        AggregateOrganizationsSql(tenant),
                        organizationParameters,
                        deadline,
                        cancellationToken
                    )
                ).AsList();
                List<TenantAggregateUnlinkedManageRow> unlinked = (
                    await QueryAsync<TenantAggregateUnlinkedManageRow>(
                        session,
                        "ReadUnlinkedManages",
                        AggregateUnlinkedManagesSql(tenant),
                        new { tenant.TenantId },
                        deadline,
                        cancellationToken
                    )
                ).AsList();
                return Outcome.Commit<SnapshotTenantAggregateResult>(
                    new SnapshotTenantAggregateResult.Success(
                        TenantAggregateAssembler.Assemble(dataStores, organizations, unlinked)
                    )
                );
            },
            diagnostic => new SnapshotTenantAggregateResult.DbFailure(diagnostic),
            cancellationToken
        );
    }

    public Task<SnapshotAcquireResult> AcquireAsync(
        int dataStoreId,
        string leaseOwner,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(leaseOwner);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(leaseOwner.Length, LeaseOwnerMaxLength);

        TenantScope tenant = CurrentTenant();
        var keys = new { DataStoreId = dataStoreId, tenant.TenantId };

        return InTransactionAsync<SnapshotAcquireResult>(
            "Acquire",
            LeaseDuration,
            _lockWait,
            async (session, deadline) =>
            {
                if (
                    await ScalarAsync<int?>(
                        session,
                        "LockDataStore",
                        LockDataStoreSql(tenant),
                        keys,
                        deadline,
                        cancellationToken
                    )
                    is null
                )
                {
                    return Outcome.Rollback<SnapshotAcquireResult>(
                        new SnapshotAcquireResult.DataStoreNotFound()
                    );
                }

                var lease = new EducationOrganizationSnapshotLease(dataStoreId, leaseOwner, 1);
                if (
                    await ScalarAsync<int?>(
                        session,
                        "LockTarget",
                        LockTargetSql(tenant, UpdateLock),
                        keys,
                        deadline,
                        cancellationToken
                    )
                    is null
                )
                {
                    // The key-range lock on the missing key holds back any concurrent first acquisition.
                    DateTime now = await ScalarAsync<DateTime>(
                        session,
                        "ReadClock",
                        ReadClockSql,
                        new { },
                        deadline,
                        cancellationToken
                    );
                    await ExecuteAsync(
                        session,
                        "InsertTarget",
                        InsertTargetSql,
                        Parameters(
                            new
                            {
                                DataStoreId = dataStoreId,
                                tenant.TenantId,
                                Owner = leaseOwner,
                                LeaseMilliseconds,
                            },
                            now
                        ),
                        deadline,
                        cancellationToken
                    );
                    return Outcome.Commit<SnapshotAcquireResult>(new SnapshotAcquireResult.Acquired(lease));
                }

                TargetRow row = await ReadTargetAsync(
                    session,
                    tenant,
                    dataStoreId,
                    deadline,
                    cancellationToken
                );
                if (row.LeaseIsLive)
                {
                    return Outcome.Rollback<SnapshotAcquireResult>(new SnapshotAcquireResult.Busy());
                }

                long generation = await ScalarAsync<long>(
                    session,
                    "TakeLease",
                    TakeLeaseSql(tenant),
                    Parameters(
                        new
                        {
                            DataStoreId = dataStoreId,
                            tenant.TenantId,
                            Owner = leaseOwner,
                            LeaseMilliseconds,
                        },
                        row.DatabaseUtcNow
                    ),
                    deadline,
                    cancellationToken
                );
                return Outcome.Commit<SnapshotAcquireResult>(
                    new SnapshotAcquireResult.Acquired(lease with { Generation = generation })
                );
            },
            diagnostic => new SnapshotAcquireResult.DbFailure(diagnostic),
            cancellationToken
        );
    }

    public Task<SnapshotTargetResult> ReclaimAsync(
        EducationOrganizationSnapshotLease lease,
        CancellationToken cancellationToken
    ) =>
        UnderLeaseAsync(
            "Reclaim",
            lease,
            LeaseDuration,
            _lockWait,
            UpdateLock,
            (session, tenant, deadline, row) =>
                DeleteInBatchesAsync(
                    session,
                    "DeleteUnpublished",
                    ReclaimSql(tenant),
                    new
                    {
                        lease.DataStoreId,
                        tenant.TenantId,
                        lease.Generation,
                        row.ActiveGeneration,
                        BatchSize,
                    },
                    deadline,
                    cancellationToken
                ),
            cancellationToken
        );

    public Task<SnapshotTargetResult> RenewAsync(
        EducationOrganizationSnapshotLease lease,
        CancellationToken cancellationToken
    ) =>
        UnderLeaseAsync(
            "Renew",
            lease,
            RenewalTimeout,
            RenewalTimeout - Min(_deadlineHeadroom, RenewalTimeout / 2),
            UpdateLock,
            (session, tenant, deadline, row) =>
                ExecuteAsync(
                    session,
                    "ExtendLease",
                    RenewSql(tenant),
                    Parameters(
                        new
                        {
                            lease.DataStoreId,
                            tenant.TenantId,
                            lease.Owner,
                            lease.Generation,
                            LeaseMilliseconds,
                        },
                        row.DatabaseUtcNow
                    ),
                    deadline,
                    cancellationToken
                ),
            cancellationToken
        );

    public Task<SnapshotTargetResult> StageAsync(
        EducationOrganizationSnapshotLease lease,
        IReadOnlyList<EducationOrganizationProjectionItem> items,
        string sourceContractVersion,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrEmpty(sourceContractVersion);

        // The rows take the TenantId of the target row, which was read under the current tenant's filter.
        return UnderLeaseAsync(
            "Stage",
            lease,
            LeaseDuration,
            _lockWait,
            ShareLock,
            (session, _, deadline, row) =>
                items.Count == 0
                    ? Task.FromResult(0)
                    : session.RunAsync(
                        "InsertOrganizations",
                        token =>
                            InsertOrganizationsAsync(
                                session.Connection,
                                session.Transaction!,
                                new StagedGeneration(
                                    lease.DataStoreId,
                                    lease.Generation,
                                    row.TenantId,
                                    row.DatabaseUtcNow,
                                    sourceContractVersion
                                ),
                                items,
                                deadline,
                                token
                            ),
                        deadline,
                        cancellationToken
                    ),
            cancellationToken
        );
    }

    public async Task<SnapshotTargetResult> ActivateAsync(
        DbTransaction transaction,
        EducationOrganizationSnapshotLease lease,
        string sourceContractVersion,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrEmpty(sourceContractVersion);
        DbConnection connection = ActiveTransactionConnection(transaction, cancellationToken);
        TenantScope tenant = CurrentTenant();

        try
        {
            if (
                await connection.ExecuteScalarAsync<int?>(
                    new CommandDefinition(
                        LockTargetSql(tenant, UpdateLock),
                        new { lease.DataStoreId, tenant.TenantId },
                        transaction,
                        cancellationToken: cancellationToken
                    )
                )
                is null
            )
            {
                return new SnapshotTargetResult.DataStoreRemoved();
            }

            TargetRow row = await connection.QuerySingleAsync<TargetRow>(
                new CommandDefinition(
                    ReadTargetSql(tenant),
                    new { lease.DataStoreId, tenant.TenantId },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            if (row.LossFor(lease) is { } reason)
            {
                return new SnapshotTargetResult.LeaseLost(reason);
            }

            DynamicParameters parameters = Parameters(
                new
                {
                    lease.DataStoreId,
                    tenant.TenantId,
                    lease.Owner,
                    lease.Generation,
                    SourceContractVersion = sourceContractVersion,
                },
                row.DatabaseUtcNow
            );
            int rows = await connection.ExecuteAsync(
                new CommandDefinition(
                    ActivateSql(tenant),
                    parameters,
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            return new SnapshotTargetResult.Success(rows);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new SnapshotStoreException(MssqlJobDiagnostics.From(exception, "Activate"));
        }
    }

    public Task<SnapshotTargetResult> PruneAsync(int dataStoreId, CancellationToken cancellationToken)
    {
        TenantScope tenant = CurrentTenant();
        return InTransactionAsync<SnapshotTargetResult>(
            "Prune",
            LeaseDuration,
            _lockWait,
            async (session, deadline) =>
                Outcome.Commit<SnapshotTargetResult>(
                    new SnapshotTargetResult.Success(
                        await DeleteInBatchesAsync(
                            session,
                            "DeleteSuperseded",
                            PruneSql(tenant),
                            new
                            {
                                DataStoreId = dataStoreId,
                                tenant.TenantId,
                                BatchSize,
                            },
                            deadline,
                            cancellationToken
                        )
                    )
                ),
            diagnostic => new SnapshotTargetResult.DbFailure(diagnostic),
            cancellationToken
        );
    }

    public Task<SnapshotTargetResult> ReleaseAsync(
        EducationOrganizationSnapshotLease lease,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(lease);
        TenantScope tenant = CurrentTenant();
        return InTransactionAsync<SnapshotTargetResult>(
            "Release",
            _releaseTimeout,
            _releaseTimeout - _deadlineHeadroom,
            async (session, deadline) =>
                Outcome.Commit<SnapshotTargetResult>(
                    new SnapshotTargetResult.Success(
                        await ExecuteAsync(
                            session,
                            "ClearLease",
                            ReleaseSql(tenant),
                            new
                            {
                                lease.DataStoreId,
                                tenant.TenantId,
                                lease.Owner,
                                lease.Generation,
                            },
                            deadline,
                            cancellationToken
                        )
                    )
                ),
            diagnostic => new SnapshotTargetResult.DbFailure(diagnostic),
            cancellationToken
        );
    }

    /// <summary>
    /// Inserts <paramref name="items"/> as the organizations of <paramref name="generation"/> in
    /// <paramref name="transaction"/>, in statements of at most <see cref="BatchSize"/> rows that request row locks,
    /// each bounded by the time left on <paramref name="deadline"/>. Returns the rows inserted.
    /// </summary>
    internal static async Task<int> InsertOrganizationsAsync(
        DbConnection connection,
        DbTransaction transaction,
        StagedGeneration generation,
        IReadOnlyList<EducationOrganizationProjectionItem> items,
        JobDeadline deadline,
        CancellationToken cancellationToken
    )
    {
        int inserted = 0;
        foreach (EducationOrganizationProjectionItem[] batch in items.Chunk(BatchSize))
        {
            DynamicParameters parameters = new(
                new
                {
                    generation.DataStoreId,
                    generation.Generation,
                    generation.TenantId,
                    generation.SourceContractVersion,
                }
            );
            parameters.Add("Now", generation.RefreshedAt, DbType.DateTime2);
            parameters.Add(
                "Items",
                JsonSerializer.Serialize(
                    batch.Select(item => new StagedItem(
                        item.EducationOrganizationId,
                        item.NameOfInstitution,
                        item.ShortNameOfInstitution,
                        item.Discriminator,
                        item.ParentId
                    ))
                ),
                DbType.String,
                size: -1
            );
            inserted += await connection.ExecuteAsync(
                new CommandDefinition(
                    InsertOrganizationsSql,
                    parameters,
                    transaction,
                    deadline.CommandTimeoutSeconds,
                    cancellationToken: cancellationToken
                )
            );
        }

        return inserted;
    }

    /// <summary>
    /// Lock, then validate, then write: locks the current tenant's target row with <paramref name="lockHints"/>,
    /// reads it with a fresh clock in a separate statement, and runs <paramref name="write"/> only while
    /// <paramref name="lease"/> is live.
    /// </summary>
    private Task<SnapshotTargetResult> UnderLeaseAsync(
        string operation,
        EducationOrganizationSnapshotLease lease,
        TimeSpan budget,
        TimeSpan lockWait,
        string lockHints,
        Func<JobDatabaseSession, TenantScope, JobDeadline, TargetRow, Task<int>> write,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(lease);
        TenantScope tenant = CurrentTenant();
        return InTransactionAsync<SnapshotTargetResult>(
            operation,
            budget,
            lockWait,
            async (session, deadline) =>
            {
                if (
                    await ScalarAsync<int?>(
                        session,
                        "LockTarget",
                        LockTargetSql(tenant, lockHints),
                        new { lease.DataStoreId, tenant.TenantId },
                        deadline,
                        cancellationToken
                    )
                    is null
                )
                {
                    return Outcome.Rollback<SnapshotTargetResult>(
                        new SnapshotTargetResult.DataStoreRemoved()
                    );
                }

                TargetRow row = await ReadTargetAsync(
                    session,
                    tenant,
                    lease.DataStoreId,
                    deadline,
                    cancellationToken
                );
                if (row.LossFor(lease) is { } reason)
                {
                    return Outcome.Rollback<SnapshotTargetResult>(new SnapshotTargetResult.LeaseLost(reason));
                }

                return Outcome.Commit<SnapshotTargetResult>(
                    new SnapshotTargetResult.Success(await write(session, tenant, deadline, row))
                );
            },
            diagnostic => new SnapshotTargetResult.DbFailure(diagnostic),
            cancellationToken
        );
    }

    /// <summary>
    /// Runs <paramref name="body"/> in one API transaction whose lock waits are bounded by
    /// <paramref name="lockWait"/>, and the whole operation, its commit and session cleanup included, by
    /// <paramref name="budget"/>. A failure, the deadline included, becomes <paramref name="failure"/>; cancellation
    /// of the caller's token propagates. The session always ends by rolling back an open transaction and resetting
    /// <c>LOCK_TIMEOUT</c>.
    /// </summary>
    private async Task<T> InTransactionAsync<T>(
        string operation,
        TimeSpan budget,
        TimeSpan lockWait,
        Func<JobDatabaseSession, JobDeadline, Task<Outcome<T>>> body,
        Func<JobFailureDiagnostic, T> failure,
        CancellationToken cancellationToken
    )
    {
        JobDeadline deadline = JobDeadline.Start(budget);
        JobDatabaseSession session = new(
            new SqlConnection(databaseOptions.Value.DatabaseConnection),
            SessionHooks
        );
        try
        {
            await MssqlJobSession.OpenAsync(session, deadline, cancellationToken);
            await MssqlJobSession.BeginAsync(session, deadline, cancellationToken);
            await MssqlJobSession.ExecuteAsync(
                session,
                "SetLockWait",
                $"SET LOCK_TIMEOUT {(long)lockWait.TotalMilliseconds};",
                deadline,
                cancellationToken
            );
            Outcome<T> outcome = await body(session, deadline);
            if (outcome.Commit)
            {
                await MssqlJobSession.ExecuteAsync(
                    session,
                    "Commit",
                    MssqlJobSession.CommitTransaction,
                    deadline,
                    cancellationToken
                );
            }

            return outcome.Result;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return failure(MssqlJobDiagnostics.From(exception, operation));
        }
        finally
        {
            await session.EndAsync(
                deadline,
                MssqlJobSession.EndSessionWith(session, deadline, MssqlJobSession.EndSession)
            );
        }
    }

    /// <summary>
    /// The open connection of a caller's active SQL Server transaction. A completed or disposed transaction is
    /// rejected, by reading <c>IsolationLevel</c>, before any statement runs, so the activation can never run
    /// outside it.
    /// </summary>
    private static DbConnection ActiveTransactionConnection(
        DbTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not SqlTransaction)
        {
            throw new ArgumentException(
                "The supplied transaction must be a SQL Server transaction.",
                nameof(transaction)
            );
        }

        _ = transaction.IsolationLevel;
        if (transaction.Connection is not SqlConnection { State: ConnectionState.Open } connection)
        {
            throw new InvalidOperationException("The supplied transaction must have an open connection.");
        }

        return connection;
    }

    private TenantScope CurrentTenant()
    {
        TenantContext context = tenantContextProvider.Context;
        return new TenantScope(
            context,
            context is TenantContext.Multitenant multitenant ? multitenant.TenantId : null
        );
    }

    /// <summary>
    /// The parameters of a write that uses the clock value read after the row lock, typed <c>datetime2</c> so it
    /// is written back exactly rather than rounded to <c>datetime</c>.
    /// </summary>
    private static DynamicParameters Parameters(object values, DateTime now)
    {
        DynamicParameters parameters = new(values);
        parameters.Add("Now", now, DbType.DateTime2);
        return parameters;
    }

    // Never selects the connection string. Context keys are unique per data store.
    private static string RefreshTargetsSql(TenantScope tenant) =>
        $"""
            SELECT ds.Id AS DataStoreId, c.ContextKey, c.ContextValue
            FROM dmscs.DataStore AS ds
            LEFT JOIN dmscs.DataStoreContext AS c ON c.DataStoreId = ds.Id
            WHERE {tenant.Where("ds")} AND (@DataStoreId IS NULL OR ds.Id = @DataStoreId)
            ORDER BY ds.Id, c.ContextKey COLLATE Latin1_General_100_BIN2;
            """;

    private static string DataStoreExistsSql(TenantScope tenant) =>
        $"""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM dmscs.DataStore WHERE Id = @DataStoreId AND {tenant.Where()}
            ) THEN 1 ELSE 0 END AS bit);
            """;

    // (a) The target rows are read by primary-key seeks under shared locks held to commit, so an activation cannot
    // change ActiveGeneration until the read ends. The link is one-to-one and same-tenant; the joined tables are
    // filtered by tenant all the same.
    private static string AggregateDataStoresSql(TenantScope tenant) =>
        $"""
            SELECT ds.Id, ds.Name, ds.DataStoreType, m.Id AS DataStoreManageId, m.Status, m.Template,
                m.TargetDatabaseName, m.HistoricalDataStoreId, t.ActiveGeneration
            FROM dmscs.DataStore AS ds
            LEFT JOIN dmscs.DataStoreManage AS m ON m.DataStoreId = ds.Id AND {tenant.Where("m")}
            LEFT JOIN dmscs.EducationOrganizationSnapshotTarget AS t
                WITH (HOLDLOCK, ROWLOCK, FORCESEEK (PK_EducationOrganizationSnapshotTarget (DataStoreId)))
                ON t.DataStoreId = ds.Id AND {tenant.Where("t")}
            WHERE {tenant.Where("ds")}
            ORDER BY ds.Id;
            """;

    // (b) Only the generations statement (a) found active, each reached by a seek within its own key range.
    private static string AggregateOrganizationsSql(TenantScope tenant) =>
        $"""
            SELECT s.DataStoreId, s.EducationOrganizationId, s.NameOfInstitution, s.ShortNameOfInstitution,
                s.Discriminator, s.ParentId
            FROM OPENJSON(@Active)
                WITH (DataStoreId INT '$.DataStoreId', Generation BIGINT '$.Generation') AS active
            INNER JOIN dmscs.EducationOrganizationSnapshot AS s WITH (FORCESEEK)
                ON s.DataStoreId = active.DataStoreId AND s.Generation = active.Generation
            WHERE {tenant.Where("s")}
            ORDER BY s.DataStoreId, s.EducationOrganizationId;
            """;

    // (c) Every status, Deleted included. A linked record never dangles, so these are all the unlinked ones.
    private static string AggregateUnlinkedManagesSql(TenantScope tenant) =>
        $"""
            SELECT Id, Name, Status, Template, TargetDatabaseName, HistoricalDataStoreId
            FROM dmscs.DataStoreManage
            WHERE DataStoreId IS NULL AND {tenant.Where()}
            ORDER BY Id;
            """;

    /// <summary>Groups the ordered rows by data store, keeping the order.</summary>
    private static List<EducationOrganizationRefreshTarget> RefreshTargets(
        IEnumerable<TargetContextRow> rows
    ) =>
        rows.GroupBy(row => row.DataStoreId)
            .Select(group => new EducationOrganizationRefreshTarget(
                group.Key,
                group
                    .Where(row => row.ContextKey is not null)
                    .ToDictionary(row => row.ContextKey!, row => row.ContextValue!, StringComparer.Ordinal)
            ))
            .ToList();

    private static string LockDataStoreSql(TenantScope tenant) =>
        $"""
            SELECT 1 FROM dmscs.DataStore WITH (HOLDLOCK, ROWLOCK, FORCESEEK)
            WHERE Id = @DataStoreId AND {tenant.Where()};
            """;

    private static string LockTargetSql(TenantScope tenant, string lockHints) =>
        $"""
            SELECT 1 FROM dmscs.EducationOrganizationSnapshotTarget WITH ({lockHints})
            WHERE DataStoreId = @DataStoreId AND {tenant.Where()};
            """;

    // Read after the row lock is held, so the clock sample postdates any wait for it.
    private static string ReadTargetSql(TenantScope tenant) =>
        $"""
            SELECT TenantId, LastGeneration, ActiveGeneration, LeaseOwner, LeaseGeneration, LeaseExpiresAt,
                SYSUTCDATETIME() AS DatabaseUtcNow
            FROM dmscs.EducationOrganizationSnapshotTarget WITH (FORCESEEK)
            WHERE DataStoreId = @DataStoreId AND {tenant.Where()};
            """;

    // Assignments read the row's values from before the update, so the lease takes the new LastGeneration.
    private static string TakeLeaseSql(TenantScope tenant) =>
        $"""
            UPDATE dmscs.EducationOrganizationSnapshotTarget
            SET LastGeneration = LastGeneration + 1,
                LeaseOwner = @Owner,
                LeaseGeneration = LastGeneration + 1,
                LeaseExpiresAt = DATEADD(millisecond, @LeaseMilliseconds, @Now),
                LastModifiedAt = @Now
            OUTPUT inserted.LeaseGeneration
            WHERE DataStoreId = @DataStoreId AND {tenant.Where()};
            """;

    private static string LeasePredicate(TenantScope tenant) =>
        $"""
            DataStoreId = @DataStoreId AND {tenant.Where()}
                AND LeaseOwner = @Owner AND LeaseGeneration = @Generation
            """;

    private static string RenewSql(TenantScope tenant) =>
        $"""
            UPDATE dmscs.EducationOrganizationSnapshotTarget
            SET LeaseExpiresAt = DATEADD(millisecond, @LeaseMilliseconds, @Now),
                LastModifiedAt = @Now
            WHERE {LeasePredicate(tenant)};
            """;

    private static string ActivateSql(TenantScope tenant) =>
        $"""
            UPDATE dmscs.EducationOrganizationSnapshotTarget
            SET ActiveGeneration = @Generation,
                RefreshedAt = @Now,
                SourceContractVersion = @SourceContractVersion,
                LeaseOwner = NULL,
                LeaseGeneration = NULL,
                LeaseExpiresAt = NULL,
                LastModifiedAt = @Now
            WHERE {LeasePredicate(tenant)};
            """;

    private static string ReleaseSql(TenantScope tenant) =>
        $"""
            UPDATE dmscs.EducationOrganizationSnapshotTarget
            SET LeaseOwner = NULL,
                LeaseGeneration = NULL,
                LeaseExpiresAt = NULL,
                LastModifiedAt = SYSUTCDATETIME()
            WHERE {LeasePredicate(tenant)};
            """;

    // Keeps the lease's own generation and the active one; an absent active generation keeps nothing else.
    private static string ReclaimSql(TenantScope tenant) =>
        DeleteBatchSql(
            $"""
            SELECT TOP (@BatchSize) k.DataStoreId, k.Generation, k.EducationOrganizationId
            FROM dmscs.EducationOrganizationSnapshot AS k WITH (ROWLOCK, FORCESEEK)
            WHERE k.DataStoreId = @DataStoreId AND {tenant.Where("k")}
                  AND k.Generation <> @Generation
                  AND (@ActiveGeneration IS NULL OR k.Generation <> @ActiveGeneration)
            """
        );

    private static string PruneSql(TenantScope tenant) =>
        DeleteBatchSql(
            $"""
            SELECT TOP (@BatchSize) k.DataStoreId, k.Generation, k.EducationOrganizationId
            FROM dmscs.EducationOrganizationSnapshotTarget AS t WITH (FORCESEEK)
            INNER JOIN dmscs.EducationOrganizationSnapshot AS k WITH (ROWLOCK, FORCESEEK)
                ON k.DataStoreId = t.DataStoreId
            WHERE t.DataStoreId = @DataStoreId AND k.DataStoreId = @DataStoreId
              AND {tenant.Where("t")} AND {tenant.Where("k")}
              AND k.Generation < t.ActiveGeneration
            """
        );

    /// <summary>
    /// One batched delete of the rows whose keys <paramref name="batchSelect"/> returns. SQL Server refuses
    /// <c>FORCESEEK</c> on a <c>DELETE</c>'s target, so the keys are chosen by a separate reference that seeks
    /// within the data store's key range, and the target is joined to them on its full primary key under
    /// <c>OPTION (LOOP JOIN)</c>. The intent is that the target is reached by one seek per chosen key and never
    /// scanned past other data stores' rows. The hint fixes only the join algorithm, not the access path or order
    /// the optimizer picks for the target, so this is the plan the large reclaim and prune tests observe, not one
    /// SQL Server guarantees.
    /// </summary>
    private static string DeleteBatchSql(string batchSelect) =>
        $"""
            DELETE s
            FROM dmscs.EducationOrganizationSnapshot AS s WITH (ROWLOCK)
            INNER JOIN (
                {batchSelect}
            ) AS batch
                ON s.DataStoreId = batch.DataStoreId
                AND s.Generation = batch.Generation
                AND s.EducationOrganizationId = batch.EducationOrganizationId
            OPTION (LOOP JOIN);
            """;

    /// <summary>
    /// Runs a <c>DELETE TOP (@BatchSize)</c> statement, in the session's transaction, until a batch deletes fewer
    /// rows; returns the total.
    /// </summary>
    private static async Task<int> DeleteInBatchesAsync(
        JobDatabaseSession session,
        string name,
        string sql,
        object parameters,
        JobDeadline deadline,
        CancellationToken cancellationToken
    )
    {
        int total = 0;
        int deleted;
        do
        {
            deleted = await ExecuteAsync(session, name, sql, parameters, deadline, cancellationToken);
            total += deleted;
        } while (deleted == BatchSize);

        return total;
    }

    private static Task<TargetRow> ReadTargetAsync(
        JobDatabaseSession session,
        TenantScope tenant,
        int dataStoreId,
        JobDeadline deadline,
        CancellationToken cancellationToken
    ) =>
        session.RunAsync(
            "ReadTarget",
            token =>
                session.Connection.QuerySingleAsync<TargetRow>(
                    MssqlJobSession.Command(
                        session,
                        ReadTargetSql(tenant),
                        new { DataStoreId = dataStoreId, tenant.TenantId },
                        deadline,
                        token
                    )
                ),
            deadline,
            cancellationToken
        );

    private static Task<T?> ScalarAsync<T>(
        JobDatabaseSession session,
        string name,
        string sql,
        object parameters,
        JobDeadline deadline,
        CancellationToken cancellationToken
    ) =>
        session.RunAsync(
            name,
            token =>
                session.Connection.ExecuteScalarAsync<T>(
                    MssqlJobSession.Command(session, sql, parameters, deadline, token)
                ),
            deadline,
            cancellationToken
        );

    private static Task<IEnumerable<T>> QueryAsync<T>(
        JobDatabaseSession session,
        string name,
        string sql,
        object parameters,
        JobDeadline deadline,
        CancellationToken cancellationToken
    ) =>
        session.RunAsync(
            name,
            token =>
                session.Connection.QueryAsync<T>(
                    MssqlJobSession.Command(session, sql, parameters, deadline, token)
                ),
            deadline,
            cancellationToken
        );

    private static Task<int> ExecuteAsync(
        JobDatabaseSession session,
        string name,
        string sql,
        object parameters,
        JobDeadline deadline,
        CancellationToken cancellationToken
    ) =>
        session.RunAsync(
            name,
            token =>
                session.Connection.ExecuteAsync(
                    MssqlJobSession.Command(session, sql, parameters, deadline, token)
                ),
            deadline,
            cancellationToken
        );

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    /// <summary>The generation an insert writes: its data store, tenant, staging time and contract version.</summary>
    internal sealed record StagedGeneration(
        int DataStoreId,
        long Generation,
        long? TenantId,
        DateTime RefreshedAt,
        string SourceContractVersion
    );

    /// <summary>One organization as serialized into the insert's JSON parameter.</summary>
    private sealed record StagedItem(
        long Id,
        string Name,
        string? ShortName,
        string Discriminator,
        long? ParentId
    );

    /// <summary>The tenant context one operation captured, and its <c>TenantId</c> parameter value.</summary>
    private sealed record TenantScope(TenantContext Context, long? TenantId)
    {
        public string Where(string? tableAlias = null) => Context.TenantWhereClause(tableAlias);
    }

    /// <summary>A data store and one of its contexts, or null context columns when it has none.</summary>
    private sealed record TargetContextRow(int DataStoreId, string? ContextKey, string? ContextValue);

    /// <summary>An active generation as serialized into the organizations statement's JSON parameter.</summary>
    private sealed record ActiveGeneration(int DataStoreId, long Generation);

    /// <summary>A transaction body's result and whether the transaction commits.</summary>
    private readonly record struct Outcome<T>(T Result, bool Commit);

    private static class Outcome
    {
        public static Outcome<T> Commit<T>(T result) => new(result, true);

        public static Outcome<T> Rollback<T>(T result) => new(result, false);
    }

    /// <summary>
    /// The locked target row and the database clock read after the lock was held. The times are UTC in
    /// <c>DATETIME2</c> columns, which SqlClient reads as <see cref="DateTimeKind.Unspecified"/>; they are only
    /// compared with each other and written back.
    /// </summary>
    private sealed record TargetRow(
        long? TenantId,
        long LastGeneration,
        long? ActiveGeneration,
        string? LeaseOwner,
        long? LeaseGeneration,
        DateTime? LeaseExpiresAt,
        DateTime DatabaseUtcNow
    )
    {
        public bool LeaseIsLive => LeaseOwner is not null && LeaseExpiresAt > DatabaseUtcNow;

        /// <summary>Why <paramref name="lease"/> does not authorize its holder, or null while it is live.</summary>
        public SnapshotLeaseLossReason? LossFor(EducationOrganizationSnapshotLease lease)
        {
            if (
                !string.Equals(LeaseOwner, lease.Owner, StringComparison.Ordinal)
                || LeaseGeneration != lease.Generation
            )
            {
                return SnapshotLeaseLossReason.Taken;
            }

            return LeaseExpiresAt > DatabaseUtcNow ? null : SnapshotLeaseLossReason.Expired;
        }
    }
}
