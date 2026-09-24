// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;

/// <summary>Creates PostgreSQL fences bound to one execution's claim and ownership gate (spec D-5).</summary>
public sealed class PostgresqlJobFenceFactory(
    IOptions<DatabaseOptions> databaseOptions,
    JobLeaseTimings timings
) : IJobFenceFactory
{
    /// <summary>Test seam: hooks into every session a fence opens.</summary>
    internal JobDatabaseSessionHooks? SessionHooks { get; init; }

    public IJobFence Create(ClaimedJob job, JobExecutionOwnership ownership) =>
        new PostgresqlJobFence(
            databaseOptions.Value.DatabaseConnection,
            timings,
            job,
            ownership,
            SessionHooks
        );

    /// <summary>
    /// One execution's fence (D-5). Under the execution gate it locks the job row, validates ownership with
    /// fresh database time in a separate statement, runs the consumer's work in the same transaction under a
    /// deadline taken from that time, revalidates ownership, and commits only while ownership is certain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Validation is a separate statement because PostgreSQL evaluates the <c>WHERE</c> clause and select list
    /// of a <c>SELECT … FOR UPDATE</c> before it waits for the lock when the holder releases the row
    /// unchanged, so a lease that expired during the wait would still look live in that statement.
    /// </para>
    /// <para>
    /// While the fence holds the execution gate, every database wait runs through a
    /// <see cref="JobDatabaseSession"/>: the acquisition under its own timeout, and the revalidation, the commit,
    /// and the rollback of a transaction left open under the fence deadline, which the fence therefore never
    /// outlasts. A commit whose wait the deadline ends has an unknown outcome and makes the execution uncertain.
    /// </para>
    /// </remarks>
    private sealed class PostgresqlJobFence(
        string connectionString,
        JobLeaseTimings timings,
        ClaimedJob job,
        JobExecutionOwnership ownership,
        JobDatabaseSessionHooks? hooks
    ) : IJobFence
    {
        private static readonly string _setFenceLockWait =
            $"SET LOCAL lock_timeout = '{(long)JobLeaseTimings.FenceLockWait.TotalMilliseconds}ms';";

        private const string LockJobRow = """SELECT "Id" FROM "dmscs"."Job" WHERE "Id" = @Id FOR UPDATE;""";

        private const string OwnedLease = """
            WITH t AS (SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS now)
            SELECT j."LeaseExpiresAt" - t.now AS "Remaining"
            FROM "dmscs"."Job" AS j, t
            WHERE j."Id" = @Id AND j."LeaseOwner" = @Owner AND j."FencingToken" = @Token
              AND j."Status" = 'InProgress' AND j."LeaseExpiresAt" > t.now;
            """;

        public async Task ExecuteAsync(
            Func<DbTransaction, CancellationToken, Task> work,
            CancellationToken cancellationToken
        )
        {
            // (1) The gate admits a waiting renewal first; a lost or uncertain ownership never reaches the
            // database.
            using IDisposable gate = await ownership.EnterAsync(cancellationToken);
            if (ownership.State != JobOwnershipState.Owned)
            {
                throw new JobLeaseLostException();
            }

            FenceState state = new(
                new JobDatabaseSession(new NpgsqlConnection(connectionString), hooks),
                JobDeadline.Start(JobLeaseTimings.FenceAcquisitionTimeout)
            );
            try
            {
                await ExecuteLockedAsync(state, work, cancellationToken);
            }
            finally
            {
                // A transaction left open is rolled back within the current deadline, never by disposal.
                await state.Session.EndAsync(
                    state.Deadline,
                    state.Committed ? null : PostgresqlJobSession.Rollback(state.Session)
                );
            }
        }

        private async Task ExecuteLockedAsync(
            FenceState state,
            Func<DbTransaction, CancellationToken, Task> work,
            CancellationToken cancellationToken
        )
        {
            JobDatabaseSession session = state.Session;

            // (2) Open and begin, bounded like the acquisition.
            try
            {
                await PostgresqlJobSession.OpenAsync(session, state.Deadline, cancellationToken);
                await PostgresqlJobSession.BeginAsync(session, state.Deadline, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new JobFenceUnavailableException();
            }

            // (3) Acquisition, bounded by its own timeout rather than the fence deadline.
            state.Deadline = JobDeadline.Start(JobLeaseTimings.FenceAcquisitionTimeout);
            TimeSpan remainingLease = await AcquireAsync(state, cancellationToken);

            // (4) One deadline, taken from fresh database time after the lock is held, covers the work, the
            // checks, the revalidation, the commit, and the rollback. It is never raised.
            TimeSpan fenceTimeout = Min(
                timings.FenceTimeout,
                remainingLease - JobLeaseTimings.FenceLeaseReserve
            );
            state.Deadline = JobDeadline.Start(fenceTimeout);
            using JobDeadlineCancellation deadline = state.Deadline.CreateCancellation(cancellationToken);

            await work(session.Transaction!, deadline.Token);

            // (5) The work returned: ownership must still be certain and the deadline not reached.
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested || ownership.State != JobOwnershipState.Owned)
            {
                throw new JobLeaseLostException();
            }

            // (6) Revalidation with fresh time, within the deadline.
            await RevalidateAsync(state, cancellationToken);

            // (7) A commit whose outcome is unknown, its wait ended by the deadline included, makes this execution
            // uncertain. It is never retried.
            try
            {
                await PostgresqlJobSession.CommitAsync(session, state.Deadline, cancellationToken);
                state.Committed = true;
            }
            catch (Exception)
            {
                ownership.TryMarkUncertain("FenceCommitUnknown");
                throw new JobLeaseLostException();
            }
        }

        private async Task<TimeSpan> AcquireAsync(FenceState state, CancellationToken cancellationToken)
        {
            JobDatabaseSession session = state.Session;
            JobDeadline acquisition = state.Deadline;
            try
            {
                await PostgresqlJobSession.ExecuteAsync(
                    session,
                    "SetLockWait",
                    _setFenceLockWait,
                    acquisition,
                    cancellationToken
                );
                await session.RunAsync(
                    "LockRow",
                    token =>
                        session.Connection.ExecuteScalarAsync<long?>(
                            PostgresqlJobSession.Command(
                                session,
                                LockJobRow,
                                new { Id = job.Id },
                                acquisition,
                                token
                            )
                        ),
                    acquisition,
                    cancellationToken
                );
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                throw new JobFenceUnavailableException();
            }
            catch (TimeoutException)
            {
                throw new JobFenceUnavailableException();
            }

            TimeSpan? remaining = await session.RunAsync(
                "ValidateLease",
                token =>
                    session.Connection.QuerySingleOrDefaultAsync<TimeSpan?>(
                        PostgresqlJobSession.Command(
                            session,
                            OwnedLease,
                            OwnershipParameters,
                            acquisition,
                            token
                        )
                    ),
                acquisition,
                cancellationToken
            );

            return remaining is { } lease && lease >= JobLeaseTimings.FenceMinimumRemainingLease
                ? lease
                : throw new JobLeaseLostException();
        }

        private async Task RevalidateAsync(FenceState state, CancellationToken cancellationToken)
        {
            JobDatabaseSession session = state.Session;
            JobDeadline fenceDeadline = state.Deadline;
            TimeSpan? remaining;
            try
            {
                remaining = await session.RunAsync(
                    "RevalidateLease",
                    token =>
                        session.Connection.QuerySingleOrDefaultAsync<TimeSpan?>(
                            PostgresqlJobSession.Command(
                                session,
                                OwnedLease,
                                OwnershipParameters,
                                fenceDeadline,
                                token
                            )
                        ),
                    fenceDeadline,
                    cancellationToken
                );
            }
            catch (Exception) when (fenceDeadline.Expired && !cancellationToken.IsCancellationRequested)
            {
                // The deadline ended the revalidation.
                throw new JobLeaseLostException();
            }

            if (remaining is null)
            {
                throw new JobLeaseLostException();
            }
        }

        private object OwnershipParameters =>
            new
            {
                Id = job.Id,
                Owner = job.LeaseOwner,
                Token = job.FencingToken,
            };

        private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

        /// <summary>
        /// The fence's session, the deadline that bounds its current step and its rollback, and whether it
        /// committed.
        /// </summary>
        private sealed class FenceState(JobDatabaseSession session, JobDeadline deadline)
        {
            public JobDatabaseSession Session { get; } = session;

            public JobDeadline Deadline { get; set; } = deadline;

            public bool Committed { get; set; }
        }
    }
}
