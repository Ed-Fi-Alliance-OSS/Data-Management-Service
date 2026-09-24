// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Jobs;

/// <summary>Creates SQL Server fences bound to one execution's claim and ownership gate (spec D-5).</summary>
public sealed class MssqlJobFenceFactory(IOptions<DatabaseOptions> databaseOptions, JobLeaseTimings timings)
    : IJobFenceFactory
{
    /// <summary>Test seam: the statement that commits a fence, so a test can make the commit stall.</summary>
    internal string CommitStatement { get; init; } = MssqlJobSession.CommitTransaction;

    /// <summary>Test seam: the statement that ends a fence's session, so a test can make cleanup stall.</summary>
    internal string EndSessionStatement { get; init; } = MssqlJobSession.EndSession;

    /// <summary>Test seam: hooks into every session a fence opens.</summary>
    internal JobDatabaseSessionHooks? SessionHooks { get; init; }

    public IJobFence Create(ClaimedJob job, JobExecutionOwnership ownership) =>
        new MssqlJobFence(databaseOptions.Value.DatabaseConnection, timings, job, ownership, this);

    /// <summary>
    /// One execution's fence (D-5). Under the execution gate it locks the job row, validates ownership with
    /// fresh database time in a separate batch, runs the consumer's work in the same transaction under a
    /// deadline taken from that time, revalidates ownership, and commits only while ownership is certain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Validation is a separate batch because SQL Server reads <c>SYSUTCDATETIME()</c> when a statement starts,
    /// so in a statement that waited for the row lock a lease that expired during the wait would still look live.
    /// <c>LOCK_TIMEOUT</c> is session-scoped: <see cref="JobLeaseTimings.FenceLockWait"/> applies to the whole
    /// fence transaction, the consumer's work included, and is reset to <c>-1</c> when the session ends.
    /// </para>
    /// <para>
    /// While the fence holds the execution gate, every database wait runs through a
    /// <see cref="JobDatabaseSession"/>: the acquisition under its own timeout, and the revalidation, the commit,
    /// and the session cleanup under the fence deadline, which the fence therefore never outlasts. A commit whose
    /// wait the deadline ends has an unknown outcome and makes the execution uncertain.
    /// </para>
    /// </remarks>
    private sealed class MssqlJobFence(
        string connectionString,
        JobLeaseTimings timings,
        ClaimedJob job,
        JobExecutionOwnership ownership,
        MssqlJobFenceFactory factory
    ) : IJobFence
    {
        private const int LockTimeoutErrorNumber = 1222;

        private static readonly string _setFenceLockWait =
            $"SET LOCK_TIMEOUT {(long)JobLeaseTimings.FenceLockWait.TotalMilliseconds};";

        private const string LockJobRow =
            "SELECT Id FROM dmscs.Job WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE Id = @Id;";

        private const string OwnedLease = """
            DECLARE @Now DATETIME2 = SYSUTCDATETIME();
            SELECT LeaseExpiresAt, @Now AS DatabaseUtcNow
            FROM dmscs.Job
            WHERE Id = @Id AND LeaseOwner = @Owner AND FencingToken = @Token AND Status = N'InProgress'
              AND LeaseExpiresAt > @Now;
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
                new JobDatabaseSession(new SqlConnection(connectionString), factory.SessionHooks),
                JobDeadline.Start(JobLeaseTimings.FenceAcquisitionTimeout)
            );
            try
            {
                await ExecuteLockedAsync(state, work, cancellationToken);
            }
            finally
            {
                // The fence set LOCK_TIMEOUT, so the session always needs its cleanup, within the current deadline.
                await state.Session.EndAsync(
                    state.Deadline,
                    MssqlJobSession.EndSessionWith(state.Session, state.Deadline, factory.EndSessionStatement)
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

            // (2) Open and begin, bounded like the acquisition. The consumer's work needs an API transaction.
            try
            {
                await MssqlJobSession.OpenAsync(session, state.Deadline, cancellationToken);
                await MssqlJobSession.BeginAsync(session, state.Deadline, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new JobFenceUnavailableException();
            }

            // (3) Acquisition, bounded by its own timeout rather than the fence deadline.
            state.Deadline = JobDeadline.Start(JobLeaseTimings.FenceAcquisitionTimeout);
            TimeSpan remainingLease = await AcquireAsync(state, cancellationToken);

            // (4) One deadline, taken from fresh database time after the lock is held, covers the work, the
            // checks, the revalidation, the commit, and the session cleanup. It is never raised.
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
                await MssqlJobSession.ExecuteAsync(
                    session,
                    "Commit",
                    factory.CommitStatement,
                    state.Deadline,
                    cancellationToken
                );
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
                await MssqlJobSession.ExecuteAsync(
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
                            MssqlJobSession.Command(
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
            catch (SqlException exception) when (exception.Number == LockTimeoutErrorNumber)
            {
                throw new JobFenceUnavailableException();
            }
            catch (TimeoutException)
            {
                throw new JobFenceUnavailableException();
            }

            LeaseRow? lease = await session.RunAsync(
                "ValidateLease",
                token =>
                    session.Connection.QuerySingleOrDefaultAsync<LeaseRow>(
                        MssqlJobSession.Command(session, OwnedLease, OwnershipParameters, acquisition, token)
                    ),
                acquisition,
                cancellationToken
            );

            return lease is not null && lease.Remaining >= JobLeaseTimings.FenceMinimumRemainingLease
                ? lease.Remaining
                : throw new JobLeaseLostException();
        }

        private async Task RevalidateAsync(FenceState state, CancellationToken cancellationToken)
        {
            JobDatabaseSession session = state.Session;
            JobDeadline fenceDeadline = state.Deadline;
            LeaseRow? lease;
            try
            {
                lease = await session.RunAsync(
                    "RevalidateLease",
                    token =>
                        session.Connection.QuerySingleOrDefaultAsync<LeaseRow>(
                            MssqlJobSession.Command(
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

            if (lease is null)
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

        /// <summary>The fence's session and the deadline that bounds its current step and its cleanup.</summary>
        private sealed class FenceState(JobDatabaseSession session, JobDeadline deadline)
        {
            public JobDatabaseSession Session { get; } = session;

            public JobDeadline Deadline { get; set; } = deadline;
        }

        private sealed record LeaseRow(DateTime LeaseExpiresAt, DateTime DatabaseUtcNow)
        {
            public TimeSpan Remaining => LeaseExpiresAt - DatabaseUtcNow;
        }
    }
}
