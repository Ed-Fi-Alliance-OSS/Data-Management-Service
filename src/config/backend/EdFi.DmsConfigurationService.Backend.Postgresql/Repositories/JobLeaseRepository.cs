// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

/// <summary>
/// PostgreSQL claims and ownership-dependent writes for <c>dmscs.Job</c> (spec D-3, D-4, D-6). Failures are
/// returned as results carrying a <see cref="JobFailureDiagnostic"/>; nothing here logs a message or a value.
/// </summary>
/// <remarks>
/// Every ownership-dependent write runs as one transaction that first takes the row lock, waiting at most
/// <see cref="JobLeaseTimings.WriteLockWait"/>, and then runs the guarded <c>UPDATE</c> as a separate statement,
/// so its <c>clock_timestamp()</c> is read after the lock is held: a lease that expired while the write waited
/// is never authorized. One deadline, <see cref="JobLeaseTimings.RenewalTimeout"/>, bounds the whole write, its
/// commit and rollback included; each statement's command timeout is derived from the time left on it, it is
/// never extended, and every wait runs through a <see cref="JobDatabaseSession"/>, so the caller never waits past
/// it even when Npgsql has not finished (<see cref="PostgresqlJobSession"/>). A write that fails before the
/// guarded <c>UPDATE</c> is sent is <c>FailureUnknown</c> (nothing written); one that fails after it is sent is
/// <c>ResultUnknown</c>, which the caller treats as uncertainty and never retries.
/// </remarks>
public sealed class JobLeaseRepository(IOptions<DatabaseOptions> databaseOptions, JobLeaseTimings timings)
    : IJobLeaseRepository
{
    private const string SystemUser = "system";

    private static readonly string _setWriteLockWait =
        $"SET LOCAL lock_timeout = '{(long)JobLeaseTimings.WriteLockWait.TotalMilliseconds}ms';";

    private const string LockJobRow = """SELECT "Id" FROM "dmscs"."Job" WHERE "Id" = @Id FOR UPDATE;""";

    // The ownership predicate P (D-4), with one fresh time sample t.now taken after the row lock is held.
    private const string FreshTime = """WITH t AS (SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS now)""";

    private const string OwnershipPredicate = """
        j."Id" = @Id AND j."LeaseOwner" = @Owner AND j."FencingToken" = @Token AND j."Status" = 'InProgress'
          AND j."LeaseExpiresAt" > t.now
        """;

    private const string RenewSql = $"""
        {FreshTime}
        UPDATE "dmscs"."Job" AS j
        SET "LeaseExpiresAt" = t.now + @LeaseSeconds * interval '1 second',
            "LastModifiedAt" = t.now,
            "ModifiedBy" = @Owner
        FROM t
        WHERE {OwnershipPredicate}
        RETURNING j."LeaseExpiresAt", t.now AS "DatabaseUtcNow";
        """;

    private const string CompleteSql = $"""
        {FreshTime}
        UPDATE "dmscs"."Job" AS j
        SET "Status" = 'Completed',
            "FinishedAt" = t.now,
            "LeaseOwner" = NULL,
            "LeaseExpiresAt" = NULL,
            "LastModifiedAt" = t.now,
            "ModifiedBy" = @Owner
        FROM t
        WHERE {OwnershipPredicate}
        RETURNING NULL::timestamp AS "LeaseExpiresAt", t.now AS "DatabaseUtcNow";
        """;

    private const string FailTransientSql = $"""
        {FreshTime}
        UPDATE "dmscs"."Job" AS j
        SET "Status" = 'Pending',
            "NextAttemptAt" = t.now + @BackoffSeconds * interval '1 second',
            "FinishedAt" = NULL,
            "LeaseOwner" = NULL,
            "LeaseExpiresAt" = NULL,
            "LastModifiedAt" = t.now,
            "ModifiedBy" = @Owner
        FROM t
        WHERE {OwnershipPredicate}
        RETURNING NULL::timestamp AS "LeaseExpiresAt", t.now AS "DatabaseUtcNow";
        """;

    private const string FailTerminalSql = $"""
        {FreshTime}
        UPDATE "dmscs"."Job" AS j
        SET "Status" = 'Error',
            "FinishedAt" = t.now,
            "ErrorMessage" = @ErrorMessage,
            "LeaseOwner" = NULL,
            "LeaseExpiresAt" = NULL,
            "LastModifiedAt" = t.now,
            "ModifiedBy" = @Owner
        FROM t
        WHERE {OwnershipPredicate}
        RETURNING NULL::timestamp AS "LeaseExpiresAt", t.now AS "DatabaseUtcNow";
        """;

    private const string ReleaseToPendingSql = $"""
        {FreshTime}
        UPDATE "dmscs"."Job" AS j
        SET "Status" = 'Pending',
            "NextAttemptAt" = t.now,
            "LeaseOwner" = NULL,
            "LeaseExpiresAt" = NULL,
            "LastModifiedAt" = t.now,
            "ModifiedBy" = @Owner
        FROM t
        WHERE {OwnershipPredicate}
        RETURNING NULL::timestamp AS "LeaseExpiresAt", t.now AS "DatabaseUtcNow";
        """;

    // D-3 (A1): an ordered walk of IX_Job_Claim (NextAttemptAt, Id) that locks one row and skips locked rows,
    // so it never waits and the statement's start time is fresh enough for the eligibility test. The
    // redundant Status IN conjunct keeps the predicate aligned with the SQL Server filtered index.
    private const string ClaimSql = """
        WITH candidate AS (
            SELECT "Id"
            FROM "dmscs"."Job"
            WHERE (("Status" = 'Pending' AND "NextAttemptAt" <= (now() AT TIME ZONE 'UTC'))
                OR ("Status" = 'InProgress' AND "LeaseExpiresAt" <= (now() AT TIME ZONE 'UTC')))
              AND "Status" IN ('Pending', 'InProgress')
              AND "AttemptCount" < @MaxAttempts
            ORDER BY "NextAttemptAt", "Id"
            LIMIT 1
            FOR UPDATE SKIP LOCKED
        )
        UPDATE "dmscs"."Job" AS j
        SET "Status" = 'InProgress',
            "LeaseOwner" = @Owner,
            "LeaseExpiresAt" = (now() AT TIME ZONE 'UTC') + @LeaseSeconds * interval '1 second',
            "FencingToken" = j."FencingToken" + 1,
            "AttemptCount" = j."AttemptCount" + 1,
            "LastModifiedAt" = (now() AT TIME ZONE 'UTC'),
            "ModifiedBy" = @Owner
        FROM candidate
        WHERE j."Id" = candidate."Id"
        RETURNING j."Id", j."JobId", j."TenantId", j."JobType", j."PayloadVersion", j."Payload" AS "PayloadJson",
            j."AttemptCount", j."FencingToken", j."LeaseOwner", j."LeaseExpiresAt", j."CreatedAt", j."NextAttemptAt",
            (now() AT TIME ZONE 'UTC') AS "DatabaseUtcNow";
        """;

    // D-6 (A4): one batch of at most @BatchSize rows, skipping locked rows, so a live lease or a row another
    // transaction holds is never touched.
    private const string ExhaustBatchSql = """
        UPDATE "dmscs"."Job" AS j
        SET "Status" = 'Error',
            "FinishedAt" = (now() AT TIME ZONE 'UTC'),
            "ErrorMessage" = @ErrorMessage,
            "FencingToken" = j."FencingToken" + 1,
            "LeaseOwner" = NULL,
            "LeaseExpiresAt" = NULL,
            "LastModifiedAt" = (now() AT TIME ZONE 'UTC'),
            "ModifiedBy" = @ModifiedBy
        WHERE j."Id" IN (
            SELECT "Id"
            FROM "dmscs"."Job"
            WHERE "AttemptCount" >= @MaxAttempts
              AND ("Status" = 'Pending'
                OR ("Status" = 'InProgress' AND "LeaseExpiresAt" <= (now() AT TIME ZONE 'UTC')))
              AND "Status" IN ('Pending', 'InProgress')
            LIMIT @BatchSize
            FOR UPDATE SKIP LOCKED
        );
        """;

    /// <summary>Test seam: runs after each committed <c>Exhaust</c> batch with the rows it changed.</summary>
    internal Action<int>? AfterExhaustBatch { get; init; }

    /// <summary>
    /// Test seam: runs after an ownership write's guarded <c>UPDATE</c> matched its row and before the commit,
    /// with the caller's token.
    /// </summary>
    internal Func<CancellationToken, Task>? BeforeOwnershipCommit { get; init; }

    /// <summary>Test seam: hooks into every session this repository opens.</summary>
    internal JobDatabaseSessionHooks? SessionHooks { get; init; }

    public async Task<JobClaimResult> ClaimNext(
        string owner,
        int leaseSeconds,
        int maxAttempts,
        CancellationToken cancellationToken
    )
    {
        JobDeadline budget = JobDeadline.Start(JobLeaseTimings.ClaimCommandTimeout);
        JobDatabaseSession session = NewSession();
        try
        {
            await PostgresqlJobSession.OpenAsync(session, budget, cancellationToken);
            ClaimRow? row = await session.RunAsync(
                "Claim",
                token =>
                    session.Connection.QuerySingleOrDefaultAsync<ClaimRow>(
                        PostgresqlJobSession.Command(
                            session,
                            ClaimSql,
                            new
                            {
                                Owner = owner,
                                LeaseSeconds = leaseSeconds,
                                MaxAttempts = maxAttempts,
                            },
                            budget,
                            token
                        )
                    ),
                budget,
                cancellationToken
            );

            return row is null
                ? new JobClaimResult.NoneAvailable()
                : new JobClaimResult.Claimed(row.ToClaimedJob());
        }
        catch (Exception exception)
        {
            return new JobClaimResult.FailureUnknown(PostgresqlJobDiagnostics.From(exception, "ClaimNext"));
        }
        finally
        {
            // An autocommitted statement leaves no transaction open.
            await session.EndAsync(budget, null);
        }
    }

    public async Task<JobExhaustResult> Exhaust(
        int maxAttempts,
        JobErrorCode errorCode,
        CancellationToken cancellationToken
    )
    {
        int exhausted = 0;
        JobDeadline budget = JobDeadline.Start(JobLeaseTimings.ClaimCommandTimeout);
        JobDatabaseSession session = NewSession();
        try
        {
            await PostgresqlJobSession.OpenAsync(session, budget, cancellationToken);

            // Each batch is one autocommitted statement, and one deadline bounds it. The stopping token is checked
            // immediately before every batch, the first included, and is not passed to the batch, so a batch that
            // has started commits or fails on its own, and every committed batch stays committed when the sweep
            // stops.
            while (!cancellationToken.IsCancellationRequested)
            {
                JobDeadline batch = JobDeadline.Start(JobLeaseTimings.ClaimCommandTimeout);
                budget = batch;
                int rows;
                try
                {
                    rows = await RunExhaustBatchAsync(session, maxAttempts, errorCode, batch);
                }
                catch (Exception exception)
                {
                    // A batch's failure, a cancellation it reports included, is never a clean stop, even during
                    // shutdown: the batch's outcome is unknown.
                    return new JobExhaustResult.FailureUnknown(
                        PostgresqlJobDiagnostics.From(exception, "Exhaust")
                    );
                }

                exhausted += rows;
                AfterExhaustBatch?.Invoke(rows);

                if (rows < JobLeaseTimings.ExhaustBatchSize)
                {
                    break;
                }
            }

            return new JobExhaustResult.Success(exhausted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only connection acquisition reaches here with the stopping token cancelled: the sweep stopped
            // before its first batch.
            return new JobExhaustResult.Success(exhausted);
        }
        catch (Exception exception)
        {
            return new JobExhaustResult.FailureUnknown(PostgresqlJobDiagnostics.From(exception, "Exhaust"));
        }
        finally
        {
            // Autocommitted batches leave no transaction open.
            await session.EndAsync(budget, null);
        }
    }

    private static Task<int> RunExhaustBatchAsync(
        JobDatabaseSession session,
        int maxAttempts,
        JobErrorCode errorCode,
        JobDeadline batch
    ) =>
        session.RunAsync(
            "ExhaustBatch",
            token =>
                session.Connection.ExecuteAsync(
                    PostgresqlJobSession.Command(
                        session,
                        ExhaustBatchSql,
                        new
                        {
                            MaxAttempts = maxAttempts,
                            ErrorMessage = errorCode.Message,
                            ModifiedBy = SystemUser,
                            BatchSize = JobLeaseTimings.ExhaustBatchSize,
                        },
                        batch,
                        token
                    )
                ),
            batch,
            CancellationToken.None
        );

    public Task<JobWriteResult> Renew(
        long id,
        string owner,
        long fencingToken,
        int leaseSeconds,
        CancellationToken cancellationToken
    ) =>
        OwnershipWriteAsync(
            "Renew",
            RenewSql,
            new
            {
                Id = id,
                Owner = owner,
                Token = fencingToken,
                LeaseSeconds = leaseSeconds,
            },
            id,
            cancellationToken
        );

    public Task<JobWriteResult> Complete(
        long id,
        string owner,
        long fencingToken,
        CancellationToken cancellationToken
    ) =>
        OwnershipWriteAsync(
            "Complete",
            CompleteSql,
            new
            {
                Id = id,
                Owner = owner,
                Token = fencingToken,
            },
            id,
            cancellationToken
        );

    public Task<JobWriteResult> FailTransient(
        long id,
        string owner,
        long fencingToken,
        int backoffSeconds,
        CancellationToken cancellationToken
    ) =>
        OwnershipWriteAsync(
            "FailTransient",
            FailTransientSql,
            new
            {
                Id = id,
                Owner = owner,
                Token = fencingToken,
                BackoffSeconds = backoffSeconds,
            },
            id,
            cancellationToken
        );

    public Task<JobWriteResult> FailTerminal(
        long id,
        string owner,
        long fencingToken,
        JobErrorCode errorCode,
        CancellationToken cancellationToken
    ) =>
        OwnershipWriteAsync(
            "FailTerminal",
            FailTerminalSql,
            new
            {
                Id = id,
                Owner = owner,
                Token = fencingToken,
                ErrorMessage = errorCode.Message,
            },
            id,
            cancellationToken
        );

    public Task<JobWriteResult> ReleaseToPending(
        long id,
        string owner,
        long fencingToken,
        CancellationToken cancellationToken
    ) =>
        OwnershipWriteAsync(
            "ReleaseToPending",
            ReleaseToPendingSql,
            new
            {
                Id = id,
                Owner = owner,
                Token = fencingToken,
            },
            id,
            cancellationToken
        );

    /// <summary>
    /// D-4 lock-then-validate under one <see cref="JobLeaseTimings.RenewalTimeout"/> deadline, which also bounds the
    /// commit and the rollback of a transaction left open.
    /// </summary>
    private async Task<JobWriteResult> OwnershipWriteAsync(
        string operation,
        string guardedUpdateSql,
        object parameters,
        long id,
        CancellationToken cancellationToken
    )
    {
        JobDeadline budget = JobDeadline.Start(timings.RenewalTimeout);
        JobDatabaseSession session = NewSession();

        bool guardedWriteSent = false;
        bool committed = false;
        try
        {
            await PostgresqlJobSession.OpenAsync(session, budget, cancellationToken);
            await PostgresqlJobSession.BeginAsync(session, budget, cancellationToken);
            await PostgresqlJobSession.ExecuteAsync(
                session,
                "SetLockWait",
                _setWriteLockWait,
                budget,
                cancellationToken
            );
            await session.RunAsync(
                "LockRow",
                token =>
                    session.Connection.ExecuteScalarAsync<long?>(
                        PostgresqlJobSession.Command(session, LockJobRow, new { Id = id }, budget, token)
                    ),
                budget,
                cancellationToken
            );

            guardedWriteSent = true;
            WrittenRow? written = await session.RunAsync(
                "GuardedWrite",
                token =>
                    session.Connection.QuerySingleOrDefaultAsync<WrittenRow>(
                        PostgresqlJobSession.Command(session, guardedUpdateSql, parameters, budget, token)
                    ),
                budget,
                cancellationToken
            );
            if (written is null)
            {
                return new JobWriteResult.OwnershipLost();
            }

            if (BeforeOwnershipCommit is { } beforeCommit)
            {
                await beforeCommit(cancellationToken);
            }

            await PostgresqlJobSession.CommitAsync(session, budget, cancellationToken);
            committed = true;
            return new JobWriteResult.Success(
                written.LeaseExpiresAt is { } leaseExpiresAt ? AsUtc(leaseExpiresAt) : null,
                AsUtc(written.DatabaseUtcNow)
            );
        }
        catch (Exception exception)
        {
            JobFailureDiagnostic diagnostic = PostgresqlJobDiagnostics.From(exception, operation);
            return guardedWriteSent
                ? new JobWriteResult.ResultUnknown(diagnostic)
                : new JobWriteResult.FailureUnknown(diagnostic);
        }
        finally
        {
            // The outcome is already decided: rolling back an open transaction only uses what is left of the
            // deadline.
            await session.EndAsync(budget, committed ? null : PostgresqlJobSession.Rollback(session));
        }
    }

    private JobDatabaseSession NewSession() =>
        new(new NpgsqlConnection(databaseOptions.Value.DatabaseConnection), SessionHooks);

    // The columns hold UTC in TIMESTAMP (without time zone), which Npgsql reads as DateTimeKind.Unspecified.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    // Positional rows: Dapper binds the columns to the constructor in order, by name and type.
    private sealed record WrittenRow(DateTime? LeaseExpiresAt, DateTime DatabaseUtcNow);

    private sealed record ClaimRow(
        long Id,
        string JobId,
        long? TenantId,
        string JobType,
        short PayloadVersion,
        string PayloadJson,
        int AttemptCount,
        long FencingToken,
        string LeaseOwner,
        DateTime LeaseExpiresAt,
        DateTime CreatedAt,
        DateTime NextAttemptAt,
        DateTime DatabaseUtcNow
    )
    {
        public ClaimedJob ToClaimedJob() =>
            new(
                Id,
                JobId,
                TenantId,
                JobType,
                PayloadVersion,
                PayloadJson,
                AttemptCount,
                FencingToken,
                LeaseOwner,
                AsUtc(LeaseExpiresAt),
                AsUtc(CreatedAt),
                AsUtc(NextAttemptAt),
                AsUtc(DatabaseUtcNow)
            );
    }
}
