// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

/// <summary>
/// SQL Server claims and ownership-dependent writes for <c>dmscs.Job</c> (spec D-3, D-4, D-6). Failures are
/// returned as results carrying a <see cref="JobFailureDiagnostic"/>; nothing here logs a message or a value.
/// </summary>
/// <remarks>
/// Every ownership-dependent write runs as one transaction that first takes the row lock, waiting at most
/// <see cref="JobLeaseTimings.WriteLockWait"/>, and then runs the guarded <c>UPDATE</c> in a separate batch that
/// samples <c>SYSUTCDATETIME()</c> once, after the lock is held: SQL Server reads <c>SYSUTCDATETIME()</c> when a
/// statement starts, so a statement that waited for the lock would test a lease against time read before the
/// wait. One deadline, <see cref="JobLeaseTimings.RenewalTimeout"/>, bounds the whole write; each statement's
/// command timeout is derived from the time left on it, and it is never extended. A write that fails before the
/// guarded <c>UPDATE</c> is sent is <c>FailureUnknown</c> (nothing written); one that fails after it is sent is
/// <c>ResultUnknown</c>, which the caller treats as uncertainty and never retries. The operation's deadline also
/// bounds the transaction's begin, commit, and cleanup, because SqlClient's own begin and commit are synchronous
/// and ignore it (<see cref="MssqlJobSession"/>); <c>LOCK_TIMEOUT</c> is session-scoped, so each write resets it to
/// <c>-1</c> within the same deadline before its connection is released.
/// </remarks>
public sealed class JobLeaseRepository(IOptions<DatabaseOptions> databaseOptions, JobLeaseTimings timings)
    : IJobLeaseRepository
{
    private const string SystemUser = "system";

    private static readonly string _setWriteLockWait =
        $"SET LOCK_TIMEOUT {(long)JobLeaseTimings.WriteLockWait.TotalMilliseconds};";

    private const string LockJobRow = "SELECT Id FROM dmscs.Job WITH (UPDLOCK, ROWLOCK) WHERE Id = @Id;";

    // The ownership predicate P (D-4) against one fresh sample, @Now, declared by the guarded batch.
    private const string FreshTime = "DECLARE @Now DATETIME2 = SYSUTCDATETIME();";

    private const string OwnershipPredicate = """
        Id = @Id AND LeaseOwner = @Owner AND FencingToken = @Token AND Status = N'InProgress'
          AND LeaseExpiresAt > @Now
        """;

    private const string RenewSql = $"""
        {FreshTime}
        UPDATE dmscs.Job
        SET LeaseExpiresAt = DATEADD(second, @LeaseSeconds, @Now),
            LastModifiedAt = @Now,
            ModifiedBy = @Owner
        OUTPUT inserted.LeaseExpiresAt, @Now AS DatabaseUtcNow
        WHERE {OwnershipPredicate};
        """;

    private const string CompleteSql = $"""
        {FreshTime}
        UPDATE dmscs.Job
        SET Status = N'Completed',
            FinishedAt = @Now,
            LeaseOwner = NULL,
            LeaseExpiresAt = NULL,
            LastModifiedAt = @Now,
            ModifiedBy = @Owner
        OUTPUT CAST(NULL AS DATETIME2) AS LeaseExpiresAt, @Now AS DatabaseUtcNow
        WHERE {OwnershipPredicate};
        """;

    private const string FailTransientSql = $"""
        {FreshTime}
        UPDATE dmscs.Job
        SET Status = N'Pending',
            NextAttemptAt = DATEADD(second, @BackoffSeconds, @Now),
            FinishedAt = NULL,
            LeaseOwner = NULL,
            LeaseExpiresAt = NULL,
            LastModifiedAt = @Now,
            ModifiedBy = @Owner
        OUTPUT CAST(NULL AS DATETIME2) AS LeaseExpiresAt, @Now AS DatabaseUtcNow
        WHERE {OwnershipPredicate};
        """;

    private const string FailTerminalSql = $"""
        {FreshTime}
        UPDATE dmscs.Job
        SET Status = N'Error',
            FinishedAt = @Now,
            ErrorMessage = @ErrorMessage,
            LeaseOwner = NULL,
            LeaseExpiresAt = NULL,
            LastModifiedAt = @Now,
            ModifiedBy = @Owner
        OUTPUT CAST(NULL AS DATETIME2) AS LeaseExpiresAt, @Now AS DatabaseUtcNow
        WHERE {OwnershipPredicate};
        """;

    private const string ReleaseToPendingSql = $"""
        {FreshTime}
        UPDATE dmscs.Job
        SET Status = N'Pending',
            NextAttemptAt = @Now,
            LeaseOwner = NULL,
            LeaseExpiresAt = NULL,
            LastModifiedAt = @Now,
            ModifiedBy = @Owner
        OUTPUT CAST(NULL AS DATETIME2) AS LeaseExpiresAt, @Now AS DatabaseUtcNow
        WHERE {OwnershipPredicate};
        """;

    // D-3 (A1): an ordered walk of IX_Job_Claim (NextAttemptAt, Id) under TOP (1) that locks one row and skips
    // locked rows, so it never waits and the statement's start time is fresh enough for the eligibility test.
    // The redundant Status IN conjunct is what lets SQL Server match the filtered index; without it the claim
    // scans the clustered index.
    private const string ClaimSql = """
        WITH candidate AS (
            SELECT TOP (1) Id, JobId, TenantId, JobType, PayloadVersion, Payload, Status, LeaseOwner, LeaseExpiresAt,
                FencingToken, AttemptCount, CreatedAt, NextAttemptAt, LastModifiedAt, ModifiedBy
            FROM dmscs.Job WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE ((Status = N'Pending' AND NextAttemptAt <= SYSUTCDATETIME())
                OR (Status = N'InProgress' AND LeaseExpiresAt <= SYSUTCDATETIME()))
              AND Status IN (N'Pending', N'InProgress')
              AND AttemptCount < @MaxAttempts
            ORDER BY NextAttemptAt, Id
        )
        UPDATE candidate
        SET Status = N'InProgress',
            LeaseOwner = @Owner,
            LeaseExpiresAt = DATEADD(second, @LeaseSeconds, SYSUTCDATETIME()),
            FencingToken = FencingToken + 1,
            AttemptCount = AttemptCount + 1,
            LastModifiedAt = SYSUTCDATETIME(),
            ModifiedBy = @Owner
        OUTPUT inserted.Id, inserted.JobId, inserted.TenantId, inserted.JobType, inserted.PayloadVersion,
            inserted.Payload AS PayloadJson, inserted.AttemptCount, inserted.FencingToken, inserted.LeaseOwner,
            inserted.LeaseExpiresAt, inserted.CreatedAt, inserted.NextAttemptAt, SYSUTCDATETIME() AS DatabaseUtcNow;
        """;

    // D-6 (A4): one batch of at most @BatchSize rows, skipping locked rows, so a live lease or a row another
    // transaction holds is never touched. The Status IN conjunct matches the filtered claim index.
    private const string ExhaustBatchSql = """
        UPDATE TOP (@BatchSize) dmscs.Job WITH (READPAST, ROWLOCK)
        SET Status = N'Error',
            FinishedAt = SYSUTCDATETIME(),
            ErrorMessage = @ErrorMessage,
            FencingToken = FencingToken + 1,
            LeaseOwner = NULL,
            LeaseExpiresAt = NULL,
            LastModifiedAt = SYSUTCDATETIME(),
            ModifiedBy = @ModifiedBy
        WHERE AttemptCount >= @MaxAttempts
          AND (Status = N'Pending' OR (Status = N'InProgress' AND LeaseExpiresAt <= SYSUTCDATETIME()))
          AND Status IN (N'Pending', N'InProgress');
        """;

    /// <summary>Test seam: runs after each committed <c>Exhaust</c> batch with the rows it changed.</summary>
    internal Action<int>? AfterExhaustBatch { get; init; }

    /// <summary>
    /// Test seam: runs after an ownership write's guarded <c>UPDATE</c> matched its row and before the commit,
    /// with the write's deadline token.
    /// </summary>
    internal Func<CancellationToken, Task>? BeforeOwnershipCommit { get; init; }

    /// <summary>Test seam: runs inside a claim's transaction after the claim statement, before the commit.</summary>
    internal Func<SqlConnection, DbTransaction, Task>? BeforeClaimCommit { get; init; }

    /// <summary>
    /// Test seam: runs inside each <c>Exhaust</c> batch's transaction after the batch statement, before the
    /// commit.
    /// </summary>
    internal Func<SqlConnection, DbTransaction, Task>? BeforeExhaustBatchCommit { get; init; }

    /// <summary>Test seam: the statement that commits a transaction, so a test can make a commit stall.</summary>
    internal string CommitStatement { get; init; } = MssqlJobSession.CommitTransaction;

    /// <summary>Test seam: the statement that ends a session, so a test can make cleanup stall.</summary>
    internal string EndSessionStatement { get; init; } = MssqlJobSession.EndSession;

    public async Task<JobClaimResult> ClaimNext(
        string owner,
        int leaseSeconds,
        int maxAttempts,
        CancellationToken cancellationToken
    )
    {
        // One deadline bounds the whole claim, its begin, commit, and cleanup included.
        OperationDeadline budget = OperationDeadline.Start(JobLeaseTimings.ClaimCommandTimeout);
        using CancellationTokenSource deadline = budget.CreateTokenSource(cancellationToken);
        BoundedSession session = new(new SqlConnection(databaseOptions.Value.DatabaseConnection));
        bool committed = false;
        try
        {
            await session.Connection.OpenAsync(deadline.Token);

            // One statement in its own transaction, as in autocommit; the explicit transaction only gives the
            // lock-footprint seam a point at which the claim's locks are still held.
            await session.BeginAsync(budget, deadline.Token);
            ClaimRow? row = await session.Connection.QuerySingleOrDefaultAsync<ClaimRow>(
                session.Command(
                    ClaimSql,
                    new
                    {
                        Owner = owner,
                        LeaseSeconds = leaseSeconds,
                        MaxAttempts = maxAttempts,
                    },
                    budget,
                    deadline.Token
                )
            );

            if (BeforeClaimCommit is { } beforeCommit)
            {
                await beforeCommit(session.Connection, session.Transaction!);
            }

            await session.ExecuteAsync(CommitStatement, budget, deadline.Token);
            committed = true;
            return row is null
                ? new JobClaimResult.NoneAvailable()
                : new JobClaimResult.Claimed(row.ToClaimedJob());
        }
        catch (Exception exception)
        {
            return new JobClaimResult.FailureUnknown(MssqlJobDiagnostics.From(exception, "ClaimNext"));
        }
        finally
        {
            await session.EndAsync(budget, EndSessionStatement, sessionClean: committed);
        }
    }

    public async Task<JobExhaustResult> Exhaust(
        int maxAttempts,
        JobErrorCode errorCode,
        CancellationToken cancellationToken
    )
    {
        int exhausted = 0;
        OperationDeadline budget = OperationDeadline.Start(JobLeaseTimings.ClaimCommandTimeout);
        BoundedSession session = new(new SqlConnection(databaseOptions.Value.DatabaseConnection));
        bool sessionClean = true;
        try
        {
            using (CancellationTokenSource opening = budget.CreateTokenSource(cancellationToken))
            {
                await session.Connection.OpenAsync(opening.Token);
            }

            // Each batch is one statement in its own short transaction, and one deadline bounds the batch, its
            // begin and commit included. The stopping token is checked immediately before every batch, the first
            // included, and is not passed to the batch, so a batch that has started commits or fails on its own,
            // and every committed batch stays committed when the sweep stops.
            while (!cancellationToken.IsCancellationRequested)
            {
                budget = OperationDeadline.Start(JobLeaseTimings.ClaimCommandTimeout);
                using CancellationTokenSource batch = budget.CreateTokenSource(CancellationToken.None);
                sessionClean = false;

                await session.BeginAsync(budget, batch.Token);
                int rows = await session.Connection.ExecuteAsync(
                    session.Command(
                        ExhaustBatchSql,
                        new
                        {
                            MaxAttempts = maxAttempts,
                            ErrorMessage = errorCode.Message,
                            ModifiedBy = SystemUser,
                            BatchSize = JobLeaseTimings.ExhaustBatchSize,
                        },
                        budget,
                        batch.Token
                    )
                );

                if (BeforeExhaustBatchCommit is { } beforeCommit)
                {
                    await beforeCommit(session.Connection, session.Transaction!);
                }

                await session.ExecuteAsync(CommitStatement, budget, batch.Token);
                sessionClean = true;

                exhausted += rows;
                AfterExhaustBatch?.Invoke(rows);

                if (rows < JobLeaseTimings.ExhaustBatchSize)
                {
                    break;
                }
            }

            return new JobExhaustResult.Success(exhausted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && sessionClean)
        {
            // Only connection acquisition observes the stopping token, so no batch was running: the sweep
            // stopped before its next batch.
            return new JobExhaustResult.Success(exhausted);
        }
        catch (Exception exception)
        {
            return new JobExhaustResult.FailureUnknown(MssqlJobDiagnostics.From(exception, "Exhaust"));
        }
        finally
        {
            await session.EndAsync(budget, EndSessionStatement, sessionClean);
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

    /// <summary>
    /// D-4 lock-then-validate under one <see cref="JobLeaseTimings.RenewalTimeout"/> deadline, which also bounds the
    /// commit and the session cleanup.
    /// </summary>
    private async Task<JobWriteResult> OwnershipWriteAsync(
        string operation,
        string guardedUpdateSql,
        object parameters,
        long id,
        CancellationToken cancellationToken
    )
    {
        OperationDeadline budget = OperationDeadline.Start(timings.RenewalTimeout);
        using CancellationTokenSource deadline = budget.CreateTokenSource(cancellationToken);
        BoundedSession session = new(new SqlConnection(databaseOptions.Value.DatabaseConnection));

        bool guardedWriteSent = false;
        try
        {
            await session.Connection.OpenAsync(deadline.Token);
            await session.BeginAsync(budget, deadline.Token);
            await session.ExecuteAsync(_setWriteLockWait, budget, deadline.Token);
            await session.Connection.ExecuteScalarAsync<long?>(
                session.Command(LockJobRow, new { Id = id }, budget, deadline.Token)
            );

            guardedWriteSent = true;
            WrittenRow? written = await session.Connection.QuerySingleOrDefaultAsync<WrittenRow>(
                session.Command(guardedUpdateSql, parameters, budget, deadline.Token)
            );
            if (written is null)
            {
                return new JobWriteResult.OwnershipLost();
            }

            if (BeforeOwnershipCommit is { } beforeCommit)
            {
                await beforeCommit(deadline.Token);
            }

            // Bounded by the deadline even once it has started: a commit it ends has an unknown outcome.
            await session.ExecuteAsync(CommitStatement, budget, deadline.Token);
            return new JobWriteResult.Success(
                written.LeaseExpiresAt is { } leaseExpiresAt ? AsUtc(leaseExpiresAt) : null,
                AsUtc(written.DatabaseUtcNow)
            );
        }
        catch (Exception exception)
        {
            JobFailureDiagnostic diagnostic = MssqlJobDiagnostics.From(exception, operation);
            return guardedWriteSent
                ? new JobWriteResult.ResultUnknown(diagnostic)
                : new JobWriteResult.FailureUnknown(diagnostic);
        }
        finally
        {
            // The outcome is already decided: ending the session only uses what is left of the deadline.
            await session.EndAsync(budget, EndSessionStatement);
        }
    }

    internal static int Seconds(TimeSpan timeout) => (int)Math.Ceiling(timeout.TotalSeconds);

    // The columns hold UTC in DATETIME2, which SqlClient reads as DateTimeKind.Unspecified.
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
