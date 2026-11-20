// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;
using EdFi.DmsConfigurationService.Backend.Services;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

/// <summary>
/// PostgreSQL education organization snapshots and their target leases.
/// </summary>
/// <remarks>
/// <para>
/// Each operation is one transaction. A lease decision locks the target row first (<c>FOR UPDATE</c>, or
/// <c>FOR SHARE</c> while staging), then reads <c>clock_timestamp()</c> with the locked row in a separate statement,
/// because PostgreSQL evaluates a locking statement's own <c>WHERE</c> clause and select list before it waits for a
/// row whose holder releases it unchanged. The write then uses that clock value. Acquisition first takes a key-share
/// lock on the data store row, so a concurrent delete either finishes first, and the data store is not found, or
/// waits until the acquisition commits and then cascades.
/// </para>
/// <para>
/// Each operation reads the tenant context once and filters every statement it issues, lookups and writes alike,
/// by that tenant's <c>TenantId</c>, so a target of another partition behaves as absent. This keeps partitions
/// apart; it is not an authorization boundary.
/// </para>
/// <para>
/// Every wait runs through a <see cref="JobDatabaseSession"/> under a deadline. A renewal's deadline is
/// <c>JobSettings:RenewalInterval / 2</c> and its server-side lock wait one second shorter, so a lock wait normally
/// ends in the lock-timeout error (<c>55P03</c>); when the deadline ends the wait first, the session is handed over
/// and the operation fails in the same way, with a <see cref="TimeoutException"/> diagnostic. Release has 5 seconds,
/// with a 4-second lock wait. Every other operation has the lease duration, with a 5-second lock wait. Activation
/// runs in the job fence's transaction, under the fence's lock wait and deadline.
/// </para>
/// </remarks>
public sealed class EducationOrganizationSnapshotRepository(
    IOptions<DatabaseOptions> databaseOptions,
    IOptions<JobOptions> jobOptions,
    ITenantContextProvider tenantContextProvider
) : IEducationOrganizationSnapshotRepository
{
    private const int LeaseOwnerMaxLength = 32;

    private static readonly TimeSpan _lockWait = JobLeaseTimings.WriteLockWait;
    private static readonly TimeSpan _releaseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _deadlineHeadroom = TimeSpan.FromSeconds(1);

    // The data store was found in the current tenant under a key-share lock, so the target takes its tenant.
    private const string InsertTargetSql = """
        INSERT INTO dmscs."EducationOrganizationSnapshotTarget" ("DataStoreId", "TenantId", "LastGeneration")
        VALUES (@DataStoreId, @TenantId, 0)
        ON CONFLICT ("DataStoreId") DO NOTHING;
        """;

    private const string StageSql = """
        INSERT INTO dmscs."EducationOrganizationSnapshot" (
            "DataStoreId", "Generation", "TenantId", "EducationOrganizationId", "NameOfInstitution",
            "ShortNameOfInstitution", "Discriminator", "ParentId", "RefreshedAt", "SourceContractVersion")
        SELECT @DataStoreId, @Generation, @TenantId, item.id, item.name, item.short_name, item.discriminator,
            item.parent_id, @Now, @SourceContractVersion
        FROM unnest(@Ids, @Names, @ShortNames, @Discriminators, @ParentIds)
            AS item (id, name, short_name, discriminator, parent_id);
        """;

    /// <summary>Test seam: hooks into every session this repository opens.</summary>
    internal JobDatabaseSessionHooks? SessionHooks { get; init; }

    private TimeSpan LeaseDuration => jobOptions.Value.LeaseDuration;

    private TimeSpan RenewalTimeout => jobOptions.Value.RenewalTimeout;

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

                await ExecuteAsync(
                    session,
                    "InsertTarget",
                    InsertTargetSql,
                    keys,
                    deadline,
                    cancellationToken
                );
                await ScalarAsync<int?>(
                    session,
                    "LockTarget",
                    LockTargetSql(tenant, "UPDATE"),
                    keys,
                    deadline,
                    cancellationToken
                );
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
                    new
                    {
                        DataStoreId = dataStoreId,
                        tenant.TenantId,
                        Owner = leaseOwner,
                        Now = row.DatabaseUtcNow,
                        LeaseMilliseconds = LeaseDuration.TotalMilliseconds,
                    },
                    deadline,
                    cancellationToken
                );
                return Outcome.Commit<SnapshotAcquireResult>(
                    new SnapshotAcquireResult.Acquired(
                        new EducationOrganizationSnapshotLease(dataStoreId, leaseOwner, generation)
                    )
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
            "UPDATE",
            (session, tenant, deadline, row) =>
                ExecuteAsync(
                    session,
                    "DeleteUnpublished",
                    ReclaimSql(tenant),
                    new
                    {
                        lease.DataStoreId,
                        tenant.TenantId,
                        lease.Generation,
                        row.ActiveGeneration,
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
            "UPDATE",
            (session, tenant, deadline, row) =>
                ExecuteAsync(
                    session,
                    "ExtendLease",
                    RenewSql(tenant),
                    new
                    {
                        lease.DataStoreId,
                        tenant.TenantId,
                        lease.Owner,
                        lease.Generation,
                        Now = row.DatabaseUtcNow,
                        LeaseMilliseconds = LeaseDuration.TotalMilliseconds,
                    },
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
            "SHARE",
            (session, _, deadline, row) =>
                items.Count == 0
                    ? Task.FromResult(0)
                    : session.RunAsync(
                        "InsertOrganizations",
                        async token =>
                        {
                            await using NpgsqlCommand command = StageCommand(
                                session,
                                lease,
                                row,
                                items,
                                sourceContractVersion,
                                deadline
                            );
                            return await command.ExecuteNonQueryAsync(token);
                        },
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
                        LockTargetSql(tenant, "UPDATE"),
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

            int rows = await connection.ExecuteAsync(
                new CommandDefinition(
                    ActivateSql(tenant),
                    new
                    {
                        lease.DataStoreId,
                        tenant.TenantId,
                        lease.Owner,
                        lease.Generation,
                        Now = row.DatabaseUtcNow,
                        SourceContractVersion = sourceContractVersion,
                    },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
            return new SnapshotTargetResult.Success(rows);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new SnapshotStoreException(PostgresqlJobDiagnostics.From(exception, "Activate"));
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
                        await ExecuteAsync(
                            session,
                            "DeleteSuperseded",
                            PruneSql(tenant),
                            new { DataStoreId = dataStoreId, tenant.TenantId },
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
    /// Lock, then validate, then write: locks the current tenant's target row <c>FOR</c>
    /// <paramref name="lockMode"/>, reads it with a fresh clock in a separate statement, and runs
    /// <paramref name="write"/> only while <paramref name="lease"/> is live.
    /// </summary>
    private Task<SnapshotTargetResult> UnderLeaseAsync(
        string operation,
        EducationOrganizationSnapshotLease lease,
        TimeSpan budget,
        TimeSpan lockWait,
        string lockMode,
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
                        LockTargetSql(tenant, lockMode),
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
    /// Runs <paramref name="body"/> in one transaction whose lock waits are bounded by <paramref name="lockWait"/>,
    /// and the whole operation, its commit and rollback included, by <paramref name="budget"/>. A failure, the
    /// deadline included, becomes <paramref name="failure"/>; cancellation of the caller's token propagates.
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
            new NpgsqlConnection(databaseOptions.Value.DatabaseConnection),
            SessionHooks
        );
        bool committed = false;
        try
        {
            await PostgresqlJobSession.OpenAsync(session, deadline, cancellationToken);
            await PostgresqlJobSession.BeginAsync(session, deadline, cancellationToken);
            await PostgresqlJobSession.ExecuteAsync(
                session,
                "SetLockWait",
                $"SET LOCAL lock_timeout = '{(long)lockWait.TotalMilliseconds}ms';",
                deadline,
                cancellationToken
            );
            Outcome<T> outcome = await body(session, deadline);
            if (outcome.Commit)
            {
                await PostgresqlJobSession.CommitAsync(session, deadline, cancellationToken);
                committed = true;
            }

            return outcome.Result;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return failure(PostgresqlJobDiagnostics.From(exception, operation));
        }
        finally
        {
            await session.EndAsync(deadline, committed ? null : PostgresqlJobSession.Rollback(session));
        }
    }

    /// <summary>
    /// The open connection of a caller's active PostgreSQL transaction. Npgsql keeps <c>Connection</c> set after a
    /// commit or rollback and would run a command on that transaction in autocommit, so a completed or disposed
    /// transaction is rejected, by reading <c>IsolationLevel</c>, before any statement runs.
    /// </summary>
    private static DbConnection ActiveTransactionConnection(
        DbTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not NpgsqlTransaction)
        {
            throw new ArgumentException(
                "The supplied transaction must be a PostgreSQL transaction.",
                nameof(transaction)
            );
        }

        _ = transaction.IsolationLevel;
        if (transaction.Connection is not NpgsqlConnection { State: ConnectionState.Open } connection)
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

    private static string LockDataStoreSql(TenantScope tenant) =>
        $"""
            SELECT 1 FROM dmscs."DataStore" WHERE "Id" = @DataStoreId AND {tenant.Where()} FOR KEY SHARE;
            """;

    private static string LockTargetSql(TenantScope tenant, string lockMode) =>
        $"""
            SELECT 1 FROM dmscs."EducationOrganizationSnapshotTarget"
            WHERE "DataStoreId" = @DataStoreId AND {tenant.Where()}
            FOR {lockMode};
            """;

    // Read after the row lock is held, so the clock sample postdates any wait for it.
    private static string ReadTargetSql(TenantScope tenant) =>
        $"""
            SELECT "TenantId", "LastGeneration", "ActiveGeneration", "LeaseOwner", "LeaseGeneration", "LeaseExpiresAt",
                (clock_timestamp() AT TIME ZONE 'UTC') AS "DatabaseUtcNow"
            FROM dmscs."EducationOrganizationSnapshotTarget"
            WHERE "DataStoreId" = @DataStoreId AND {tenant.Where()};
            """;

    // Assignments read the row's values from before the update, so the lease takes the new LastGeneration.
    private static string TakeLeaseSql(TenantScope tenant) =>
        $"""
            UPDATE dmscs."EducationOrganizationSnapshotTarget"
            SET "LastGeneration" = "LastGeneration" + 1,
                "LeaseOwner" = @Owner,
                "LeaseGeneration" = "LastGeneration" + 1,
                "LeaseExpiresAt" = @Now + @LeaseMilliseconds * interval '1 millisecond',
                "LastModifiedAt" = @Now
            WHERE "DataStoreId" = @DataStoreId AND {tenant.Where()}
            RETURNING "LeaseGeneration";
            """;

    private static string LeasePredicate(TenantScope tenant) =>
        $"""
            "DataStoreId" = @DataStoreId AND {tenant.Where()}
                AND "LeaseOwner" = @Owner AND "LeaseGeneration" = @Generation
            """;

    private static string RenewSql(TenantScope tenant) =>
        $"""
            UPDATE dmscs."EducationOrganizationSnapshotTarget"
            SET "LeaseExpiresAt" = @Now + @LeaseMilliseconds * interval '1 millisecond',
                "LastModifiedAt" = @Now
            WHERE {LeasePredicate(tenant)};
            """;

    private static string ActivateSql(TenantScope tenant) =>
        $"""
            UPDATE dmscs."EducationOrganizationSnapshotTarget"
            SET "ActiveGeneration" = @Generation,
                "RefreshedAt" = @Now,
                "SourceContractVersion" = @SourceContractVersion,
                "LeaseOwner" = NULL,
                "LeaseGeneration" = NULL,
                "LeaseExpiresAt" = NULL,
                "LastModifiedAt" = @Now
            WHERE {LeasePredicate(tenant)};
            """;

    private static string ReleaseSql(TenantScope tenant) =>
        $"""
            UPDATE dmscs."EducationOrganizationSnapshotTarget"
            SET "LeaseOwner" = NULL,
                "LeaseGeneration" = NULL,
                "LeaseExpiresAt" = NULL,
                "LastModifiedAt" = (clock_timestamp() AT TIME ZONE 'UTC')
            WHERE {LeasePredicate(tenant)};
            """;

    // Keeps the lease's own generation and the active one; an absent active generation keeps nothing else.
    private static string ReclaimSql(TenantScope tenant) =>
        $"""
            DELETE FROM dmscs."EducationOrganizationSnapshot"
            WHERE "DataStoreId" = @DataStoreId AND {tenant.Where()}
              AND "Generation" <> @Generation
              AND (@ActiveGeneration::bigint IS NULL OR "Generation" <> @ActiveGeneration::bigint);
            """;

    private static string PruneSql(TenantScope tenant) =>
        $"""
            DELETE FROM dmscs."EducationOrganizationSnapshot" AS s
            USING dmscs."EducationOrganizationSnapshotTarget" AS t
            WHERE t."DataStoreId" = @DataStoreId AND {tenant.Where("t")}
              AND s."DataStoreId" = t."DataStoreId" AND {tenant.Where("s")}
              AND s."Generation" < t."ActiveGeneration";
            """;

    private static NpgsqlCommand StageCommand(
        JobDatabaseSession session,
        EducationOrganizationSnapshotLease lease,
        TargetRow row,
        IReadOnlyList<EducationOrganizationProjectionItem> items,
        string sourceContractVersion,
        JobDeadline deadline
    )
    {
        NpgsqlCommand command = new(
            StageSql,
            (NpgsqlConnection)session.Connection,
            (NpgsqlTransaction)session.Transaction!
        )
        {
            CommandTimeout = deadline.CommandTimeoutSeconds,
        };
        command.Parameters.Add(new NpgsqlParameter<int>("DataStoreId", lease.DataStoreId));
        command.Parameters.Add(new NpgsqlParameter<long>("Generation", lease.Generation));
        command.Parameters.Add(
            new NpgsqlParameter("TenantId", NpgsqlDbType.Bigint)
            {
                Value = (object?)row.TenantId ?? DBNull.Value,
            }
        );
        command.Parameters.Add(
            new NpgsqlParameter("Now", NpgsqlDbType.Timestamp) { Value = row.DatabaseUtcNow }
        );
        command.Parameters.Add(
            new NpgsqlParameter("SourceContractVersion", NpgsqlDbType.Varchar)
            {
                Value = sourceContractVersion,
            }
        );
        command.Parameters.Add(
            ArrayParameter("Ids", "bigint[]", items.Select(item => item.EducationOrganizationId).ToArray())
        );
        command.Parameters.Add(
            ArrayParameter("Names", "varchar[]", items.Select(item => item.NameOfInstitution).ToArray())
        );
        command.Parameters.Add(
            ArrayParameter(
                "ShortNames",
                "varchar[]",
                items.Select(item => item.ShortNameOfInstitution).ToArray()
            )
        );
        command.Parameters.Add(
            ArrayParameter("Discriminators", "varchar[]", items.Select(item => item.Discriminator).ToArray())
        );
        command.Parameters.Add(
            ArrayParameter("ParentIds", "bigint[]", items.Select(item => item.ParentId).ToArray())
        );
        return command;
    }

    private static NpgsqlParameter ArrayParameter(string name, string dataTypeName, Array values) =>
        new()
        {
            ParameterName = name,
            DataTypeName = dataTypeName,
            Value = values,
        };

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
                    PostgresqlJobSession.Command(
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
                    PostgresqlJobSession.Command(session, sql, parameters, deadline, token)
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
                    PostgresqlJobSession.Command(session, sql, parameters, deadline, token)
                ),
            deadline,
            cancellationToken
        );

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    /// <summary>The tenant context one operation captured, and its <c>TenantId</c> parameter value.</summary>
    private sealed record TenantScope(TenantContext Context, long? TenantId)
    {
        public string Where(string? tableAlias = null) => Context.TenantWhereClause(tableAlias);
    }

    /// <summary>A transaction body's result and whether the transaction commits.</summary>
    private readonly record struct Outcome<T>(T Result, bool Commit);

    private static class Outcome
    {
        public static Outcome<T> Commit<T>(T result) => new(result, true);

        public static Outcome<T> Rollback<T>(T result) => new(result, false);
    }

    /// <summary>
    /// The locked target row and the database clock read after the lock was held. The times are UTC in
    /// <c>TIMESTAMP</c> columns, which Npgsql reads as <see cref="DateTimeKind.Unspecified"/>; they are only
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
