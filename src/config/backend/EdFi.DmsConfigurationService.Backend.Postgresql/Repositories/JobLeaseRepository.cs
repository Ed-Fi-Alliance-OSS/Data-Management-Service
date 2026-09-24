// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
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
/// is never authorized. One deadline, <see cref="JobLeaseTimings.RenewalTimeout"/>, bounds the whole write;
/// each statement's command timeout is derived from the time left on it, and it is never extended. A write
/// that fails before the guarded <c>UPDATE</c> is sent is <c>FailureUnknown</c> (nothing written); one that
/// fails after it is sent is <c>ResultUnknown</c>, which the caller treats as uncertainty and never retries.
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
    /// with the write's deadline token.
    /// </summary>
    internal Func<CancellationToken, Task>? BeforeOwnershipCommit { get; init; }

    public async Task<JobClaimResult> ClaimNext(
        string owner,
        int leaseSeconds,
        int maxAttempts,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using NpgsqlConnection connection = new(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync(cancellationToken);
            ClaimRow? row = await connection.QuerySingleOrDefaultAsync<ClaimRow>(
                new CommandDefinition(
                    ClaimSql,
                    new
                    {
                        Owner = owner,
                        LeaseSeconds = leaseSeconds,
                        MaxAttempts = maxAttempts,
                    },
                    commandTimeout: Seconds(JobLeaseTimings.ClaimCommandTimeout),
                    cancellationToken: cancellationToken
                )
            );

            return row is null
                ? new JobClaimResult.NoneAvailable()
                : new JobClaimResult.Claimed(row.ToClaimedJob());
        }
        catch (Exception exception)
        {
            return new JobClaimResult.FailureUnknown(PostgresqlJobDiagnostics.From(exception, "ClaimNext"));
        }
    }

    public async Task<JobExhaustResult> Exhaust(
        int maxAttempts,
        JobErrorCode errorCode,
        CancellationToken cancellationToken
    )
    {
        int exhausted = 0;
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new JobExhaustResult.Success(exhausted);
            }

            await using NpgsqlConnection connection = new(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync(CancellationToken.None);

            // Each batch is one autocommitted statement bounded by its command timeout. The stopping token is
            // checked before each batch rather than passed to it, so a batch either commits or fails on its
            // own, and every committed batch stays committed when the sweep stops.
            int rows;
            do
            {
                rows = await connection.ExecuteAsync(
                    new CommandDefinition(
                        ExhaustBatchSql,
                        new
                        {
                            MaxAttempts = maxAttempts,
                            ErrorMessage = errorCode.Message,
                            ModifiedBy = SystemUser,
                            BatchSize = JobLeaseTimings.ExhaustBatchSize,
                        },
                        commandTimeout: Seconds(JobLeaseTimings.ClaimCommandTimeout)
                    )
                );
                exhausted += rows;
                AfterExhaustBatch?.Invoke(rows);
            } while (rows == JobLeaseTimings.ExhaustBatchSize && !cancellationToken.IsCancellationRequested);

            return new JobExhaustResult.Success(exhausted);
        }
        catch (Exception exception)
        {
            return new JobExhaustResult.FailureUnknown(PostgresqlJobDiagnostics.From(exception, "Exhaust"));
        }
    }

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

    /// <summary>D-4 lock-then-validate under one <see cref="JobLeaseTimings.RenewalTimeout"/> deadline.</summary>
    private async Task<JobWriteResult> OwnershipWriteAsync(
        string operation,
        string guardedUpdateSql,
        object parameters,
        long id,
        CancellationToken cancellationToken
    )
    {
        long started = Stopwatch.GetTimestamp();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        deadline.CancelAfter(timings.RenewalTimeout);

        bool guardedWriteSent = false;
        try
        {
            await using NpgsqlConnection connection = new(databaseOptions.Value.DatabaseConnection);
            await connection.OpenAsync(deadline.Token);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
                deadline.Token
            );

            await connection.ExecuteAsync(
                DeadlineCommand(_setWriteLockWait, null, transaction, started, deadline.Token)
            );
            await connection.ExecuteScalarAsync<long?>(
                DeadlineCommand(LockJobRow, new { Id = id }, transaction, started, deadline.Token)
            );

            guardedWriteSent = true;
            WrittenRow? written = await connection.QuerySingleOrDefaultAsync<WrittenRow>(
                DeadlineCommand(guardedUpdateSql, parameters, transaction, started, deadline.Token)
            );
            if (written is null)
            {
                return new JobWriteResult.OwnershipLost();
            }

            if (BeforeOwnershipCommit is { } beforeCommit)
            {
                await beforeCommit(deadline.Token);
            }

            await transaction.CommitAsync(deadline.Token);
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
    }

    /// <summary>A statement of an ownership write, bounded by the time left on the write's deadline.</summary>
    private CommandDefinition DeadlineCommand(
        string sql,
        object? parameters,
        NpgsqlTransaction transaction,
        long started,
        CancellationToken deadline
    ) =>
        new(
            sql,
            parameters,
            transaction,
            commandTimeout: SecondsLeft(started, timings.RenewalTimeout),
            cancellationToken: deadline
        );

    internal static int Seconds(TimeSpan timeout) => (int)Math.Ceiling(timeout.TotalSeconds);

    /// <summary>
    /// A command timeout derived from the time left on a deadline, at least 1 s because 0 would mean no
    /// timeout. The deadline's own token still cancels the command at the exact deadline.
    /// </summary>
    internal static int SecondsLeft(long started, TimeSpan budget) =>
        Math.Max(1, Seconds(budget - Stopwatch.GetElapsedTime(started)));

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
